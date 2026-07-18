using BifrostQL.Core.Model;

namespace BifrostQL.Server.Grpc
{
    /// <summary>The portable artifacts produced from a <c>DbModel</c> for one profile.</summary>
    public sealed record GrpcSchemaArtifacts(
        GrpcContract Contract,
        GrpcFieldNumberManifest Manifest,
        string ProtoText,
        byte[] DescriptorSet);

    /// <summary>
    /// Builds deterministic, read-only gRPC descriptors and portable artifacts from a
    /// cached <c>DbModel</c>, per the gRPC Schema Contract ADR. The shape is discovered at
    /// runtime from the live schema; the numbering is fixed by the checked-in
    /// <see cref="GrpcFieldNumberManifest"/>. This slice produces descriptors and artifacts
    /// ONLY — no socket listener, no reflection endpoint, no execution, no writes.
    ///
    /// <para>Visibility is filtered by the same authoritative read policy the query path
    /// enforces (<see cref="GrpcSchemaVisibility"/>), so a denied table/column never
    /// appears in any artifact.</para>
    /// </summary>
    public static class GrpcSchemaGenerator
    {
        public const string Package = "bifrostql";
        public const string ServiceName = "BifrostQuery";

        /// <summary>
        /// Generate the contract, reconciled manifest, <c>.proto</c> text, and descriptor
        /// set for <paramref name="userContext"/>'s projection of <paramref name="model"/>.
        /// </summary>
        public static GrpcSchemaArtifacts Generate(
            IDbModel model,
            GrpcFieldNumberManifest manifest,
            IDictionary<string, object?> userContext)
        {
            if (model is null) throw new ArgumentNullException(nameof(model));
            if (manifest is null) throw new ArgumentNullException(nameof(manifest));

            var visible = GrpcSchemaVisibility.Project(model, userContext);
            var reconciled = manifest.Reconcile(visible);
            var contract = BuildContract(visible, reconciled);

            return new GrpcSchemaArtifacts(
                contract,
                reconciled,
                GrpcProtoTextWriter.Write(contract),
                GrpcDescriptorSetWriter.Write(contract));
        }

        /// <summary>
        /// Build the intermediate contract from the visible tables and the reconciled
        /// manifest. Tables are ordered by <c>GraphQlName</c> (never model order); a table
        /// with no primary key fails generation with a precise startup diagnostic, because a
        /// Get RPC has no way to identify a row without one.
        /// </summary>
        public static GrpcContract BuildContract(
            IReadOnlyList<GrpcVisibleTable> visible,
            GrpcFieldNumberManifest manifest)
        {
            var messages = new List<GrpcMessage>();
            var methods = new List<GrpcMethod>();
            var usesTimestamp = false;

            foreach (var v in visible.OrderBy(v => v.Table.GraphQlName, StringComparer.Ordinal))
            {
                var table = v.Table;
                var rowName = $"{table.GraphQlName}Row";

                // Key columns identify a row: ALL of them, in stable manifest order — never
                // index-zero-reduced to the first column (composite-PK compliance).
                var keyColumns = v.Columns.Where(c => c.IsPrimaryKey).ToList();
                if (keyColumns.Count == 0)
                    throw new GrpcSchemaException(
                        $"gRPC schema generation failed: table '{table.GraphQlName}' " +
                        $"({table.TableSchema}.{table.DbName}) has no primary key, so a Get RPC cannot " +
                        $"identify a row. Define a primary key on the table or hide it from the gRPC surface.");

                // Row message: one field per visible column, number from the manifest, sorted
                // by number so read order cannot change the output.
                var rowFields = new List<GrpcField>();
                foreach (var column in v.Columns)
                {
                    var kind = GrpcProtoTypeMapper.Map(column.EffectiveDataType);
                    if (kind == GrpcScalarKind.Timestamp) usesTimestamp = true;
                    var number = manifest.NumberOf(rowName, column.GraphQlName)
                        ?? throw new GrpcSchemaException(
                            $"gRPC schema generation failed: no manifest number for '{rowName}.{column.GraphQlName}'.");
                    rowFields.Add(new GrpcField(
                        Name: column.GraphQlName,
                        Number: number,
                        Scalar: kind,
                        MessageName: null,
                        Optional: column.IsNullable,   // proto3 explicit presence for nullable columns
                        Repeated: false));
                }
                messages.Add(new GrpcMessage(rowName, rowFields.OrderBy(f => f.Number).ToList()));

                // Get request: one field per key column, in manifest order, numbered 1..n.
                var getRequest = $"Get{table.GraphQlName}Request";
                var keyFields = new List<GrpcField>();
                var i = 1;
                foreach (var key in keyColumns
                             .OrderBy(k => manifest.NumberOf(rowName, k.GraphQlName) ?? int.MaxValue))
                {
                    var kind = GrpcProtoTypeMapper.Map(key.EffectiveDataType);
                    if (kind == GrpcScalarKind.Timestamp) usesTimestamp = true;
                    keyFields.Add(new GrpcField(key.GraphQlName, i++, kind, null, Optional: false, Repeated: false));
                }
                messages.Add(new GrpcMessage(getRequest, keyFields));

                var getResponse = $"Get{table.GraphQlName}Response";
                messages.Add(new GrpcMessage(getResponse, new[]
                {
                    new GrpcField("row", 1, null, rowName, Optional: false, Repeated: false),
                }));

                // List and Stream carry the SAME filter/sort/page request shape — one compiler
                // (GrpcReadRequestCompiler) consumes both, so the surfaces cannot diverge (criterion 4).
                var listRequest = $"List{table.GraphQlName}Request";
                messages.Add(new GrpcMessage(listRequest, ReadRequestFields()));

                var listResponse = $"List{table.GraphQlName}Response";
                messages.Add(new GrpcMessage(listResponse, new[]
                {
                    new GrpcField("rows", 1, null, rowName, Optional: false, Repeated: true),
                    // Opaque, position-only continuation token; absent on the last page (criterion 3).
                    new GrpcField("next_page_token", 2, GrpcScalarKind.String, null, Optional: false, Repeated: false),
                }));

                var streamRequest = $"Stream{table.GraphQlName}Request";
                messages.Add(new GrpcMessage(streamRequest, ReadRequestFields()));

                methods.Add(new GrpcMethod($"Get{table.GraphQlName}", getRequest, getResponse, ServerStreaming: false));
                methods.Add(new GrpcMethod($"List{table.GraphQlName}", listRequest, listResponse, ServerStreaming: false));
                methods.Add(new GrpcMethod($"Stream{table.GraphQlName}", streamRequest, rowName, ServerStreaming: true));
            }

            return new GrpcContract(
                Package,
                messages.OrderBy(m => m.Name, StringComparer.Ordinal).ToList(),
                new GrpcService(ServiceName, methods.OrderBy(m => m.Name, StringComparer.Ordinal).ToList()),
                usesTimestamp);
        }

        /// <summary>
        /// The read options every List/Stream request carries: a JSON <c>filter</c> (the GraphQL-shaped
        /// parameterized predicate the compiler validates against the schema), an <c>order_by</c>
        /// clause, a <c>page_size</c>, and the opaque <c>page_token</c>. Field numbers are the fixed
        /// small constants of a hand-authored request message — not manifest-managed (the manifest
        /// pins only Row columns), matching how Get request key fields are numbered.
        /// </summary>
        private static IReadOnlyList<GrpcField> ReadRequestFields() => new[]
        {
            new GrpcField("filter", 1, GrpcScalarKind.String, null, Optional: false, Repeated: false),
            new GrpcField("order_by", 2, GrpcScalarKind.String, null, Optional: false, Repeated: false),
            new GrpcField("page_size", 3, GrpcScalarKind.Int32, null, Optional: false, Repeated: false),
            new GrpcField("page_token", 4, GrpcScalarKind.String, null, Optional: false, Repeated: false),
        };
    }
}
