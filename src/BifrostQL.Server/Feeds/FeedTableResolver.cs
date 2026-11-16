using BifrostQL.Core.Model;

namespace BifrostQL.Server.Feeds
{
    /// <summary>
    /// The one rule that turns a feed name — a route segment or a scoped token's allow-list entry —
    /// into an <see cref="IDbTable"/>. Both sides go through it, so the allow-list check compares
    /// resolved tables, never spellings (AGENTS.md "Table identity is IDbTable").
    ///
    /// <para>A name matches a table by its DB name or its GraphQL name, case-insensitively. A
    /// schema-qualified <c>schema.name</c> (split on the FIRST dot) resolves exactly within that
    /// schema; a bare name resolves only when exactly one table carries it. An ambiguous name
    /// resolves to nothing, never to whichever table enumerates first: with <c>public.posts</c> and
    /// <c>internal.posts</c> both present, <c>posts</c> names neither.</para>
    /// </summary>
    internal static class FeedTableResolver
    {
        public static IDbTable? Resolve(IReadOnlyCollection<IDbTable> tables, string name)
        {
            ArgumentNullException.ThrowIfNull(tables);
            ArgumentNullException.ThrowIfNull(name);

            var dot = name.IndexOf('.');
            if (dot > 0 && dot < name.Length - 1)
            {
                var schema = name[..dot];
                var local = name[(dot + 1)..];
                var qualified = Unique(tables, t => Eq(t.TableSchema, schema) && Names(t, local), out var qualifiedHits);
                if (qualifiedHits > 0)
                    return qualified;
            }

            return Unique(tables, t => Names(t, name), out _);
        }

        private static bool Names(IDbTable table, string name)
            => Eq(table.DbName, name) || Eq(table.GraphQlName, name);

        private static bool Eq(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static IDbTable? Unique(IReadOnlyCollection<IDbTable> tables, Func<IDbTable, bool> match, out int hits)
        {
            IDbTable? found = null;
            hits = 0;
            foreach (var table in tables)
            {
                if (!match(table))
                    continue;
                hits++;
                found = table;
            }
            return hits == 1 ? found : null;
        }
    }
}
