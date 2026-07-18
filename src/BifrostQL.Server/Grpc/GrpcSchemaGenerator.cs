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
            IDictionary<string, object?> userContext,
            bool includeWrites = false)
        {
            if (model is null) throw new ArgumentNullException(nameof(model));
            if (manifest is null) throw new ArgumentNullException(nameof(manifest));

            var visible = GrpcSchemaVisibility.Project(model, userContext);
            var reconciled = manifest.Reconcile(visible);
            var contract = BuildContract(visible, reconciled, includeWrites);

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
            GrpcFieldNumberManifest manifest,
            bool includeWrites = false)
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

                // Write surface (Insert/Update/Delete) — generated ONLY when the global switch is on
                // AND the table carries the per-table 'grpc-write: enabled' allow-list metadata. A
                // table failing EITHER gate emits no mutation message or method, so its Insert/Update/
                // Delete are absent from dispatch AND reflection: unprobeable, no oracle (criterion 3).
                if (includeWrites && GrpcWriteEnabled(table))
                    usesTimestamp |= AddMutationSurface(v, keyColumns, manifest, messages, methods);
            }

            return new GrpcContract(
                Package,
                messages.OrderBy(m => m.Name, StringComparer.Ordinal).ToList(),
                new GrpcService(ServiceName, methods.OrderBy(m => m.Name, StringComparer.Ordinal).ToList()),
                usesTimestamp);
        }

        /// <summary>Whether a table opts into the gRPC write RPCs via the per-table allow-list metadata.</summary>
        private static bool GrpcWriteEnabled(IDbTable table)
            => table.CompareMetadata(MetadataKeys.Grpc.WriteEnabled, MetadataKeys.Grpc.Enabled);

        /// <summary>
        /// Emits the Insert/Update/Delete messages and methods for one write-enabled table. Request
        /// fields are numbered LOCALLY (like the Get request's key fields), not from the manifest — the
        /// manifest pins only Row columns. Every request field carries a column's proto type; a key
        /// column is required, a non-key column is proto3-optional (explicit presence) so the client
        /// sends only what it sets and the pipeline supplies defaults/required-column enforcement.
        /// Composite keys emit ALL key columns in stable manifest order — never index-zero-reduced
        /// (composite-PK compliance). Every response is a small result message carrying the affected
        /// row count (and, for insert, the generated identity).
        /// </summary>
        private static bool AddMutationSurface(
            GrpcVisibleTable v,
            IReadOnlyList<ColumnDto> keyColumns,
            GrpcFieldNumberManifest manifest,
            List<GrpcMessage> messages,
            List<GrpcMethod> methods)
        {
            var table = v.Table;
            var rowName = $"{table.GraphQlName}Row";
            var usesTimestamp = false;

            int NumberOf(ColumnDto c) => manifest.NumberOf(rowName, c.GraphQlName) ?? int.MaxValue;
            GrpcField Field(ColumnDto c, int number, bool optional)
            {
                var kind = GrpcProtoTypeMapper.Map(c.EffectiveDataType);
                if (kind == GrpcScalarKind.Timestamp) usesTimestamp = true;
                return new GrpcField(c.GraphQlName, number, kind, null, Optional: optional, Repeated: false);
            }

            var orderedKeys = keyColumns.OrderBy(NumberOf).ToList();
            var nonKeyColumns = v.Columns.Where(c => !c.IsPrimaryKey).OrderBy(NumberOf).ToList();

            // Insert request: every column, all optional (explicit presence) — client sends what it sets.
            var insertRequest = $"Insert{table.GraphQlName}Request";
            var insertFields = new List<GrpcField>();
            var next = 1;
            foreach (var column in v.Columns.OrderBy(NumberOf))
                insertFields.Add(Field(column, next++, optional: true));
            messages.Add(new GrpcMessage(insertRequest, insertFields));
            messages.Add(new GrpcMessage($"Insert{table.GraphQlName}Response", MutationResultFields()));

            // Update request: required key columns, then optional SET columns.
            var updateRequest = $"Update{table.GraphQlName}Request";
            var updateFields = new List<GrpcField>();
            next = 1;
            foreach (var key in orderedKeys)
                updateFields.Add(Field(key, next++, optional: false));
            foreach (var column in nonKeyColumns)
                updateFields.Add(Field(column, next++, optional: true));
            messages.Add(new GrpcMessage(updateRequest, updateFields));
            messages.Add(new GrpcMessage($"Update{table.GraphQlName}Response", MutationResultFields()));

            // Delete request: required key columns only. The adapter builds no predicate — the
            // positional PK plus the caller's identity is all it supplies (invariant 7b).
            var deleteRequest = $"Delete{table.GraphQlName}Request";
            var deleteFields = new List<GrpcField>();
            next = 1;
            foreach (var key in orderedKeys)
                deleteFields.Add(Field(key, next++, optional: false));
            messages.Add(new GrpcMessage(deleteRequest, deleteFields));
            messages.Add(new GrpcMessage($"Delete{table.GraphQlName}Response", MutationResultFields()));

            methods.Add(new GrpcMethod(
                $"Insert{table.GraphQlName}", insertRequest, $"Insert{table.GraphQlName}Response", ServerStreaming: false));
            methods.Add(new GrpcMethod(
                $"Update{table.GraphQlName}", updateRequest, $"Update{table.GraphQlName}Response", ServerStreaming: false));
            methods.Add(new GrpcMethod(
                $"Delete{table.GraphQlName}", deleteRequest, $"Delete{table.GraphQlName}Response", ServerStreaming: false));

            return usesTimestamp;
        }

        /// <summary>
        /// The uniform result shape every mutation RPC returns: <c>affected_rows</c> is the REAL row
        /// count the pipeline reported (0 when a tenant/policy scope narrowed the write away — the same
        /// wire answer as a genuinely-absent row, so a scoped-away write is no existence oracle);
        /// <c>returned_key</c> carries an insert's generated identity (absent on update/delete).
        /// </summary>
        private static IReadOnlyList<GrpcField> MutationResultFields() => new[]
        {
            new GrpcField("affected_rows", 1, GrpcScalarKind.Int64, null, Optional: false, Repeated: false),
            new GrpcField("returned_key", 2, GrpcScalarKind.String, null, Optional: false, Repeated: false),
        };

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
