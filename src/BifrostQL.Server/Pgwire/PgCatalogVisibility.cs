using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;

namespace BifrostQL.Server.Pgwire
{
    /// <summary>
    /// Projects an <see cref="IDbModel"/> to the tables and columns a given identity
    /// may READ, using the SAME authoritative check the query path enforces —
    /// <see cref="PolicyEvaluator"/> over the per-table <see cref="TablePolicy"/> that
    /// <see cref="PolicyConfigCollector"/> parses, with identity reconstructed by the
    /// shared <see cref="PolicyIdentity"/> projection. This is deliberately NOT a
    /// second, weaker rule: a table the caller could not <c>SELECT</c> must never be
    /// listed in the emulated <c>pg_class</c> / <c>information_schema.tables</c>, or the
    /// catalog would leak the existence of tables the identity cannot query.
    ///
    /// <para><b>Fail closed.</b> If a table's policy cannot be evaluated (malformed
    /// policy metadata, any evaluation fault) the table is EXCLUDED, never included on
    /// a "benefit of the doubt" basis. Column read-deny is applied identically, so a
    /// read-denied column is absent from the emulated <c>pg_attribute</c> /
    /// <c>information_schema.columns</c>.</para>
    ///
    /// <para>The admin role matches the query path's default: the registered
    /// <c>PolicyFilterTransformer</c> is constructed with no override, so the default
    /// <see cref="PolicyEvaluator"/> admin role applies here too. A deployment that
    /// customizes the admin role only narrows what admins see in the catalog (fail
    /// closed), never widens a non-admin's view.</para>
    /// </summary>
    internal static class PgCatalogVisibility
    {
        private static readonly PolicyEvaluator Evaluator = new();

        /// <summary>
        /// Returns the visible tables (each with its visible columns and stable catalog
        /// OIDs) for <paramref name="userContext"/>. A table whose Read is denied — or
        /// whose policy cannot be evaluated — is omitted entirely.
        /// </summary>
        public static IReadOnlyList<PgCatalogTable> Project(
            IDbModel model, IDictionary<string, object?> userContext)
        {
            if (model is null) throw new ArgumentNullException(nameof(model));
            if (userContext is null) throw new ArgumentNullException(nameof(userContext));

            var identity = PolicyIdentity.FromUserContext(userContext);
            var result = new List<PgCatalogTable>();

            foreach (var table in model.Tables)
            {
                if (!CanRead(table, identity))
                    continue;

                var columns = VisibleColumns(table, identity);
                var namespaceOid = PgCatalog.StableOid($"ns:{table.TableSchema}");
                var classOid = PgCatalog.StableOid($"cls:{table.TableSchema}.{table.DbName}");
                result.Add(new PgCatalogTable(table, columns, classOid, namespaceOid));
            }

            return result;
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
                // Fail closed: a table whose policy cannot be parsed/evaluated (e.g.
                // malformed policy-actions metadata) is hidden, never exposed.
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
                // A table that survived CanRead but whose policy now fails to parse is a
                // contradiction; treat it as no visible columns (fail closed).
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
