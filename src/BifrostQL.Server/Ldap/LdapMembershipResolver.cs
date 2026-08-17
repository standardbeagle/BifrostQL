using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Server.Ldap
{
    /// <summary>Raised when a membership fan-out exceeds its configured per-entry bound.</summary>
    internal sealed class LdapMembershipLimitException : Exception
    {
        public LdapMembershipLimitException(string message) : base(message) { }
    }

    /// <summary>
    /// Resolves the synthesized <c>member</c> and <c>memberOf</c> attributes: the DNs of the entries
    /// on the other side of a declared membership relationship.
    ///
    /// <para><b>Every leg is a transformed query intent.</b> Both hops — source keys to junction
    /// rows, junction rows to target rows — run through <see cref="IQueryIntentExecutor"/> under the
    /// BOUND session's identity. Nothing here builds SQL, and nothing here builds a scope predicate:
    /// the pipeline narrows each hop from the identity, so a member row the caller cannot see simply
    /// does not come back and its DN is never rendered. That is what makes cross-tenant membership
    /// non-leaking STRUCTURALLY — the resolver has no code that could decide to include a foreign
    /// row, in either direction.</para>
    ///
    /// <para><b>Values only come from mapped, visible targets.</b> A member DN is built by the
    /// TARGET table's own <see cref="LdapEntryTarget"/>, so it is named by the target's declared
    /// naming attribute and escaped like any other DN. A relationship whose target is not a
    /// published entry family yields no members rather than an invented DN — and model validation
    /// already refuses that configuration at startup.</para>
    ///
    /// <para><b>Composite relationships never reach here.</b> Both link kinds carry one column per
    /// leg, so a composite key would be joined on its first column alone — matching rows that agree
    /// on that column while ignoring the tenant discriminator the rest of the key carries. Model
    /// validation refuses such a relationship at startup; this class re-checks and yields nothing
    /// rather than joining partially, so the guard does not depend on validation having run.</para>
    /// </summary>
    internal sealed class LdapMembershipResolver
    {
        private readonly IQueryIntentExecutor _reads;
        private readonly LdapDirectoryIndex _index;
        private readonly LdapWireOptions _options;

        public LdapMembershipResolver(
            IQueryIntentExecutor reads, LdapDirectoryIndex index, LdapWireOptions options)
        {
            _reads = reads ?? throw new ArgumentNullException(nameof(reads));
            _index = index ?? throw new ArgumentNullException(nameof(index));
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>
        /// Resolves the <c>member</c> DNs for a page of entries of one group family, keyed by the
        /// group's own key value. Returns an empty map when the family declares no membership
        /// relationship.
        /// </summary>
        public async Task<IReadOnlyDictionary<object, IReadOnlyList<string>>> ResolveMembersAsync(
            LdapEntryTarget group,
            IReadOnlyList<IReadOnlyDictionary<string, object?>> groupRows,
            IDictionary<string, object?> userContext,
            CancellationToken ct)
        {
            var empty = (IReadOnlyDictionary<object, IReadOnlyList<string>>)
                new Dictionary<object, IReadOnlyList<string>>();

            if (group.Config.MemberRelationship is not { } relationship || groupRows.Count == 0)
                return empty;

            if (!TryResolveLink(group.Table, relationship, out var link))
                return empty;

            var targetEntry = FindTarget(link.TargetTable);
            if (targetEntry is null)
                return empty; // the target publishes no entries, so there is no DN to name

            var sourceKeys = DistinctValues(groupRows, link.SourceColumn.ColumnName);
            if (sourceKeys.Count == 0)
                return empty;

            if (link.Direct)
                return await ResolveDirectAsync(link, targetEntry, sourceKeys, userContext, ct);

            // Leg 1: junction rows for these groups, through the pipeline (the junction table's own
            // tenant/policy metadata applies too — a junction row the caller cannot see is not a
            // membership the caller can observe).
            var pairs = await FetchPairsAsync(link, sourceKeys, userContext, ct);
            if (pairs.Count == 0)
                return empty;

            // Leg 2: the member rows themselves, again through the pipeline. A member outside the
            // caller's scope is simply absent from this result.
            var namesByKey = await FetchNamingValuesAsync(
                targetEntry, link.TargetColumn, pairs.Select(p => p.TargetKey), userContext, ct);

            return BuildDns(pairs, namesByKey, targetEntry);
        }

        // The direct one-to-many: the child rows are the members, so their back-reference and their
        // naming value come from one query — through the pipeline, like every other leg.
        private async Task<IReadOnlyDictionary<object, IReadOnlyList<string>>> ResolveDirectAsync(
            LdapMembershipLink link,
            LdapEntryTarget targetEntry,
            IReadOnlyList<object> sourceKeys,
            IDictionary<string, object?> userContext,
            CancellationToken ct)
        {
            var result = new Dictionary<object, IReadOnlyList<string>>();
            if (!link.TargetTable.ColumnLookup.TryGetValue(targetEntry.NamingColumn, out var namingColumn))
                return result;

            var ceiling = checked(sourceKeys.Count * _options.MaxMembersPerEntry);
            var query = new GqlObjectQuery
            {
                DbTable = link.TargetTable,
                SchemaName = link.TargetTable.TableSchema,
                TableName = link.TargetTable.DbName,
                GraphQlName = link.TargetTable.GraphQlName,
                Path = link.TargetTable.GraphQlName,
                Filter = InFilter(link.TargetTable, link.JunctionSourceColumn, sourceKeys),
                Limit = ceiling + 1,
            };
            query.ScalarColumns.Add(new GqlObjectColumn(link.JunctionSourceColumn.ColumnName));
            query.ScalarColumns.Add(new GqlObjectColumn(namingColumn.ColumnName));

            var rows = await _reads.ExecuteAsync(
                new QueryIntent { Query = query, UserContext = userContext, Endpoint = _options.Endpoint }, ct);

            if (rows.Rows.Count > ceiling)
                throw new LdapMembershipLimitException(
                    $"membership fan-out exceeded the {_options.MaxMembersPerEntry}-member per-entry bound.");

            foreach (var bucket in rows.Rows.GroupBy(r => Scalar(r, link.JunctionSourceColumn.ColumnName)))
            {
                if (bucket.Key is null)
                    continue;
                if (bucket.Count() > _options.MaxMembersPerEntry)
                    throw new LdapMembershipLimitException(
                        $"membership fan-out exceeded the {_options.MaxMembersPerEntry}-member per-entry bound.");

                var dns = new List<string>();
                foreach (var row in bucket)
                {
                    if (!row.TryGetValue(namingColumn.ColumnName, out var value) || value is null or DBNull)
                        continue;
                    dns.Add(targetEntry.EntryDn(LdapFilterCompiler.RenderValue(namingColumn, value)));
                }
                if (dns.Count > 0)
                    result[bucket.Key] = dns;
            }
            return result;
        }

        private static Dictionary<object, IReadOnlyList<string>> BuildDns(
            IEnumerable<(object SourceKey, object TargetKey)> pairs,
            IReadOnlyDictionary<object, string> namesByKey,
            LdapEntryTarget targetEntry)
        {
            var result = new Dictionary<object, IReadOnlyList<string>>();
            foreach (var bucket in pairs.GroupBy(p => p.SourceKey))
            {
                var dns = new List<string>();
                foreach (var pair in bucket)
                {
                    if (namesByKey.TryGetValue(pair.TargetKey, out var namingValue))
                        dns.Add(targetEntry.EntryDn(namingValue));
                }
                if (dns.Count > 0)
                    result[bucket.Key] = dns;
            }
            return result;
        }

        /// <summary>
        /// Resolves the reverse <c>memberOf</c> DNs for a page of member entries: the groups whose
        /// declared membership relationship points at this family.
        /// </summary>
        public async Task<IReadOnlyDictionary<object, IReadOnlyList<string>>> ResolveMemberOfAsync(
            LdapEntryTarget member,
            IReadOnlyList<IReadOnlyDictionary<string, object?>> memberRows,
            IDictionary<string, object?> userContext,
            CancellationToken ct)
        {
            var result = new Dictionary<object, IReadOnlyList<string>>();
            if (memberRows.Count == 0)
                return result;

            foreach (var group in _index.Targets)
            {
                if (group.Config.MemberRelationship is not { } relationship)
                    continue;
                if (!TryResolveLink(group.Table, relationship, out var link))
                    continue;
                if (!ReferenceEquals(link.TargetTable, member.Table))
                    continue;

                // A direct one-to-many carries its back-reference on the member row itself, which a
                // search does not project — the reverse hop would need the member's own primary key
                // and a second single-column assumption about it. Rather than guess, memberOf is
                // simply not published for that relationship kind: an ABSENT attribute is honest,
                // whereas a partially-correct one would understate the groups an entry belongs to.
                if (link.Direct)
                    continue;

                var memberKeys = DistinctValues(memberRows, link.TargetColumn.ColumnName);
                if (memberKeys.Count == 0)
                    continue;

                // The same two legs, walked the other way. Both still run through the pipeline, so
                // a group the caller cannot see never appears in anyone's memberOf.
                var pairs = await FetchPairsAsync(link, memberKeys, userContext, ct, reverse: true);
                if (pairs.Count == 0)
                    continue;

                var namesByKey = await FetchNamingValuesAsync(
                    group, link.SourceColumn, pairs.Select(p => p.TargetKey), userContext, ct);

                foreach (var bucket in pairs.GroupBy(p => p.SourceKey))
                {
                    var dns = new List<string>();
                    foreach (var pair in bucket)
                    {
                        if (namesByKey.TryGetValue(pair.TargetKey, out var namingValue))
                            dns.Add(group.EntryDn(namingValue));
                    }
                    if (dns.Count == 0)
                        continue;

                    result[bucket.Key] = result.TryGetValue(bucket.Key, out var existing)
                        ? existing.Concat(dns).ToList()
                        : dns;
                }
            }
            return result;
        }

        /// <summary>
        /// A membership relationship reduced to the two single-column legs the join needs. Returns
        /// false for a relationship that is unknown, to-one, or COMPOSITE — the last of which is
        /// refused rather than joined on a partial key.
        /// </summary>
        private static bool TryResolveLink(IDbTable table, string relationship, out LdapMembershipLink link)
        {
            link = default!;

            if (table.ManyToManyLinks.TryGetValue(relationship, out var m2m))
            {
                if (m2m.IsComposite)
                    return false;
                return Set(out link, new LdapMembershipLink(
                    SourceColumn: m2m.SourceColumn,
                    JunctionTable: m2m.JunctionTable,
                    JunctionSourceColumn: m2m.JunctionSourceColumn,
                    JunctionTargetColumn: m2m.JunctionTargetColumn,
                    TargetTable: m2m.TargetTable,
                    TargetColumn: m2m.TargetColumn));
            }

            if (table.MultiLinks.TryGetValue(relationship, out var multi))
            {
                if (multi.IsComposite)
                    return false;
                // A direct one-to-many has no junction at all: the child rows ARE the members, so
                // one query reads both the back-reference and the naming value. Routing it through
                // the two-leg path would join the second leg on the FOREIGN KEY rather than on the
                // member row, which is a different set of rows entirely.
                return Set(out link, new LdapMembershipLink(
                    SourceColumn: multi.ParentId,
                    JunctionTable: multi.ChildTable,
                    JunctionSourceColumn: multi.ChildId,
                    JunctionTargetColumn: multi.ChildId,
                    TargetTable: multi.ChildTable,
                    TargetColumn: multi.ChildId,
                    Direct: true));
            }

            return false;

            static bool Set(out LdapMembershipLink slot, LdapMembershipLink value)
            {
                slot = value;
                return true;
            }
        }

        private LdapEntryTarget? FindTarget(IDbTable table) =>
            _index.Targets.FirstOrDefault(t => ReferenceEquals(t.Table, table));

        // Leg 1: the (source key, target key) pairs, read from the junction table through the
        // pipeline. The fan-out is bounded before the rows are consumed, not after: one over-limit
        // row is enough to know the bound is exceeded, so the query never returns an unbounded set.
        private async Task<List<(object SourceKey, object TargetKey)>> FetchPairsAsync(
            LdapMembershipLink link,
            IReadOnlyList<object> keys,
            IDictionary<string, object?> userContext,
            CancellationToken ct,
            bool reverse = false)
        {
            var lookupColumn = reverse ? link.JunctionTargetColumn : link.JunctionSourceColumn;
            var resultColumn = reverse ? link.JunctionSourceColumn : link.JunctionTargetColumn;

            var ceiling = checked(keys.Count * _options.MaxMembersPerEntry);
            var query = new GqlObjectQuery
            {
                DbTable = link.JunctionTable,
                SchemaName = link.JunctionTable.TableSchema,
                TableName = link.JunctionTable.DbName,
                GraphQlName = link.JunctionTable.GraphQlName,
                Path = link.JunctionTable.GraphQlName,
                Filter = InFilter(link.JunctionTable, lookupColumn, keys),
                Limit = ceiling + 1,
            };
            query.ScalarColumns.Add(new GqlObjectColumn(lookupColumn.ColumnName));
            query.ScalarColumns.Add(new GqlObjectColumn(resultColumn.ColumnName));

            var result = await _reads.ExecuteAsync(
                new QueryIntent { Query = query, UserContext = userContext, Endpoint = _options.Endpoint }, ct);

            if (result.Rows.Count > ceiling)
                throw new LdapMembershipLimitException(
                    $"membership fan-out exceeded the {_options.MaxMembersPerEntry}-member per-entry bound.");

            var pairs = new List<(object, object)>(result.Rows.Count);
            foreach (var row in result.Rows)
            {
                var source = Scalar(row, lookupColumn.ColumnName);
                var target = Scalar(row, resultColumn.ColumnName);
                if (source is not null && target is not null)
                    pairs.Add((source, target));
            }

            // The per-entry bound, checked per entry rather than only in aggregate: one group with
            // too many members must not pass merely because the page's other groups are small.
            foreach (var bucket in pairs.GroupBy(p => p.Item1))
            {
                if (bucket.Count() > _options.MaxMembersPerEntry)
                    throw new LdapMembershipLimitException(
                        $"membership fan-out exceeded the {_options.MaxMembersPerEntry}-member per-entry bound.");
            }

            return pairs;
        }

        // Leg 2: the naming value of each member row, again through the pipeline. A key with no row
        // here is a member the caller cannot see; it is simply absent from the map, so its DN is
        // never built.
        private async Task<Dictionary<object, string>> FetchNamingValuesAsync(
            LdapEntryTarget target,
            ColumnDto keyColumn,
            IEnumerable<object> keys,
            IDictionary<string, object?> userContext,
            CancellationToken ct)
        {
            var distinct = keys.Distinct().ToList();
            var names = new Dictionary<object, string>();
            if (distinct.Count == 0)
                return names;

            if (!target.Table.ColumnLookup.TryGetValue(target.NamingColumn, out var namingColumn))
                return names;

            var query = new GqlObjectQuery
            {
                DbTable = target.Table,
                SchemaName = target.Table.TableSchema,
                TableName = target.Table.DbName,
                GraphQlName = target.Table.GraphQlName,
                Path = target.Table.GraphQlName,
                Filter = InFilter(target.Table, keyColumn, distinct),
                Limit = distinct.Count,
            };
            query.ScalarColumns.Add(new GqlObjectColumn(keyColumn.ColumnName));
            query.ScalarColumns.Add(new GqlObjectColumn(namingColumn.ColumnName));

            var result = await _reads.ExecuteAsync(
                new QueryIntent { Query = query, UserContext = userContext, Endpoint = _options.Endpoint }, ct);

            foreach (var row in result.Rows)
            {
                var key = Scalar(row, keyColumn.ColumnName);
                if (key is null)
                    continue;
                if (!row.TryGetValue(namingColumn.ColumnName, out var value) || value is null or DBNull)
                    continue;
                names[key] = LdapFilterCompiler.RenderValue(namingColumn, value);
            }
            return names;
        }

        // The only predicate this class builds: an IN over keys it read from rows the pipeline
        // already returned. It narrows; the pipeline's own tenant/policy/soft-delete predicates are
        // ANDed on top and cannot be displaced by it.
        private static TableFilter InFilter(IDbTable table, ColumnDto column, IEnumerable<object> keys) =>
            TableFilter.FromObject(
                new Dictionary<string, object?>
                {
                    [column.GraphQlName] = new Dictionary<string, object?>
                    {
                        [FilterOperators.In] = keys.ToList(),
                    },
                },
                table.DbName);

        private static List<object> DistinctValues(
            IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, string column)
        {
            var values = new List<object>();
            var seen = new HashSet<object>();
            foreach (var row in rows)
            {
                var value = Scalar(row, column);
                if (value is not null && seen.Add(value))
                    values.Add(value);
            }
            return values;
        }

        private static object? Scalar(IReadOnlyDictionary<string, object?> row, string column) =>
            row.TryGetValue(column, out var value) && value is not (null or DBNull) ? value : null;

        private readonly record struct LdapMembershipLink(
            ColumnDto SourceColumn,
            IDbTable JunctionTable,
            ColumnDto JunctionSourceColumn,
            ColumnDto JunctionTargetColumn,
            IDbTable TargetTable,
            ColumnDto TargetColumn,
            bool Direct = false);
    }
}
