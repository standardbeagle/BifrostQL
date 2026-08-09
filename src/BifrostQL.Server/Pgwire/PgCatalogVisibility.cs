using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;

namespace BifrostQL.Server.Pgwire
{
    /// <summary>
    /// Renders the shared <see cref="SchemaReadVisibility"/> projection — the single funnel every
    /// introspection surface shares, calling the SAME evaluator the query path enforces — into the
    /// pgwire catalog's own shape (stable catalog OIDs per relation and namespace). The
    /// authorization decision, and its fail-closed handling of an unparseable policy, live in
    /// Core; only the OID assignment is pgwire's.
    ///
    /// <para>A table the caller could not <c>SELECT</c> is never listed in the emulated
    /// <c>pg_class</c> / <c>information_schema.tables</c>, and a read-denied column is absent from
    /// <c>pg_attribute</c> / <c>information_schema.columns</c>, or the catalog would leak the
    /// existence and shape of data the identity cannot query
    /// (.claude/rules/protocol-adapter-security.md invariant 4).</para>
    /// </summary>
    internal static class PgCatalogVisibility
    {
        /// <summary>
        /// Returns the visible tables (each with its visible columns and stable catalog OIDs) for
        /// <paramref name="userContext"/>. A table whose Read is denied — or whose policy cannot
        /// be evaluated — is omitted entirely.
        /// </summary>
        public static IReadOnlyList<PgCatalogTable> Project(
            IDbModel model, IDictionary<string, object?> userContext)
        {
            var visible = SchemaReadVisibility.Project(model, userContext);
            var result = new List<PgCatalogTable>(visible.Count);

            foreach (var entry in visible)
            {
                var table = entry.Table;
                var namespaceOid = PgCatalog.StableOid($"ns:{table.TableSchema}");
                var classOid = PgCatalog.StableOid($"cls:{table.TableSchema}.{table.DbName}");
                result.Add(new PgCatalogTable(table, entry.Columns, classOid, namespaceOid));
            }

            return result;
        }
    }
}
