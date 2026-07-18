using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;

namespace BifrostQL.Server.Grpc
{
    /// <summary>A table the identity may READ, with the columns it may read.</summary>
    public sealed record GrpcVisibleTable(IDbTable Table, IReadOnlyList<ColumnDto> Columns);

    /// <summary>
    /// Projects an <see cref="IDbModel"/> to the tables and columns a given identity
    /// may READ, using the SAME authoritative check the query path enforces — the
    /// exact pattern <c>PgCatalogVisibility</c> already applies for the pgwire catalog
    /// (protocol-adapter-security invariant 4). A gRPC descriptor / <c>.proto</c> /
    /// descriptor-set artifact is a schema-introspection surface: a table or column the
    /// caller could not <c>SELECT</c> must never appear in it, or the artifact leaks the
    /// existence and shape of data the identity cannot query.
    ///
    /// <para>This is deliberately NOT a second, weaker "it's just metadata" rule: it
    /// calls <see cref="PolicyEvaluator.CanAct"/> with <see cref="PolicyAction.Read"/>
    /// over <see cref="PolicyConfigCollector.FromTable"/>, and
    /// <see cref="PolicyEvaluator.IsColumnAllowed"/> with
    /// <see cref="PolicyDirection.Read"/>, under the identity reconstructed by the shared
    /// <see cref="PolicyIdentity.FromUserContext"/> — the same evaluator the data path
    /// calls. <b>Fail closed:</b> a table (or column) whose policy cannot be parsed or
    /// evaluated is EXCLUDED, never included on benefit of the doubt.</para>
    /// </summary>
    public static class GrpcSchemaVisibility
    {
        private static readonly PolicyEvaluator Evaluator = new();

        public static IReadOnlyList<GrpcVisibleTable> Project(
            IDbModel model, IDictionary<string, object?> userContext)
        {
            if (model is null) throw new ArgumentNullException(nameof(model));
            if (userContext is null) throw new ArgumentNullException(nameof(userContext));

            var identity = PolicyIdentity.FromUserContext(userContext);
            var result = new List<GrpcVisibleTable>();

            foreach (var table in model.Tables)
            {
                if (!CanRead(table, identity))
                    continue;

                result.Add(new GrpcVisibleTable(table, VisibleColumns(table, identity)));
            }

            return result;
        }

        /// <summary>
        /// Every table with all its columns, with NO policy filtering. This is used ONLY to build
        /// the runtime DISPATCH method/routing table (which method names exist) and the shared field
        /// numbering, never to decide what a caller may read — authorization is enforced per call by
        /// the transformer pipeline, and per-identity REFLECTION uses <see cref="Project"/>. So a
        /// table nobody may read still gets a route, but every call to it is scoped away and the
        /// route is never advertised in reflection.
        /// </summary>
        public static IReadOnlyList<GrpcVisibleTable> ProjectAll(IDbModel model)
        {
            if (model is null) throw new ArgumentNullException(nameof(model));
            return model.Tables
                .Select(t => new GrpcVisibleTable(t, t.Columns.ToList()))
                .ToList();
        }

        private static bool CanRead(IDbTable table, AppIdentity identity)
        {
            try
            {
                var policy = PolicyConfigCollector.FromTable(table);
                return Evaluator.CanAct(policy, PolicyAction.Read, identity).Allowed;
            }
            catch
            {
                // Fail closed: a table whose policy cannot be parsed/evaluated is hidden.
                return false;
            }
        }

        private static IReadOnlyList<ColumnDto> VisibleColumns(IDbTable table, AppIdentity identity)
        {
            TablePolicy policy;
            try
            {
                policy = PolicyConfigCollector.FromTable(table);
            }
            catch
            {
                return Array.Empty<ColumnDto>();
            }

            var result = new List<ColumnDto>();
            foreach (var column in table.Columns)
            {
                bool allowed;
                try
                {
                    allowed = Evaluator.IsColumnAllowed(policy, column.DbName, PolicyDirection.Read, identity).Allowed;
                }
                catch
                {
                    allowed = false; // fail closed on any column-evaluation fault
                }

                if (allowed)
                    result.Add(column);
            }

            return result;
        }
    }
}
