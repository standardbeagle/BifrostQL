using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;

namespace BifrostQL.Mcp
{
    /// <summary>A table the identity may READ, with the columns it may read.</summary>
    public sealed record McpVisibleTable(IDbTable Table, IReadOnlyList<ColumnDto> Columns);

    /// <summary>
    /// Projects an <see cref="IDbModel"/> to the tables and columns a given identity may
    /// READ, using the SAME authoritative check the data path enforces — the pattern
    /// <c>PgCatalogVisibility</c>, <c>GrpcSchemaVisibility</c>, and
    /// <c>ODataModelVisibility</c> already apply for their adapters
    /// (protocol-adapter-security invariant 4).
    ///
    /// <para>The MCP schema tools (<c>bifrost_schema_overview</c>,
    /// <c>bifrost_describe_table</c>) and the <c>bifrost://schema/…</c> resources are an
    /// introspection surface: a table or column the caller could not read must never
    /// appear in them, or the surface leaks the existence and shape of data the identity
    /// cannot query. This is deliberately NOT a second, weaker "it's just metadata" rule:
    /// it calls <see cref="PolicyEvaluator.CanAct"/> with <see cref="PolicyAction.Read"/>
    /// over <see cref="PolicyConfigCollector.FromTable"/>, and
    /// <see cref="PolicyEvaluator.IsColumnAllowed"/> with <see cref="PolicyDirection.Read"/>,
    /// under the identity reconstructed by the shared
    /// <see cref="PolicyIdentity.FromUserContext"/> — the same evaluator the query path
    /// calls. <b>Fail closed:</b> a table (or column) whose policy cannot be parsed or
    /// evaluated is EXCLUDED, never included on benefit of the doubt — even for an admin,
    /// since <see cref="PolicyConfigCollector.FromTable"/> throws before the evaluator's
    /// admin bypass can run.</para>
    /// </summary>
    public static class McpSchemaVisibility
    {
        private static readonly PolicyEvaluator Evaluator = new();

        /// <summary>Every table (with its readable columns) the identity behind
        /// <paramref name="userContext"/> may read.</summary>
        public static IReadOnlyList<McpVisibleTable> Project(
            IDbModel model, IDictionary<string, object?> userContext)
        {
            if (model is null) throw new ArgumentNullException(nameof(model));
            if (userContext is null) throw new ArgumentNullException(nameof(userContext));

            var identity = PolicyIdentity.FromUserContext(userContext);
            var result = new List<McpVisibleTable>();
            foreach (var table in model.Tables)
            {
                if (!CanRead(table, identity))
                    continue;
                result.Add(new McpVisibleTable(table, VisibleColumns(table, identity)));
            }
            return result;
        }

        /// <summary>
        /// The visible table matching <paramref name="tableName"/>, or null. A
        /// policy-denied table returns null exactly like a table that does not exist, so
        /// the caller's "unknown table" path is the single answer for both — introspection
        /// never becomes an existence oracle.
        /// </summary>
        public static McpVisibleTable? Find(IEnumerable<McpVisibleTable> visible, string tableName) =>
            visible.FirstOrDefault(v => string.Equals(v.Table.DbName, tableName, StringComparison.OrdinalIgnoreCase));

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
