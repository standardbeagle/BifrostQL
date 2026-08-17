using BifrostQL.Core.Model;

namespace BifrostQL.Server.Ldap
{
    /// <summary>
    /// The attribute selection a SearchRequest asked for, reduced to the three decisions RFC 4511
    /// §4.5.1.8 defines: return everything, return nothing but the DN, or return a named set.
    /// </summary>
    internal sealed class LdapAttributeSelection
    {
        /// <summary>The wildcard requesting all user attributes.</summary>
        public const string AllUserAttributes = "*";

        /// <summary>The OID requesting NO attributes — the DN alone.</summary>
        public const string NoAttributes = "1.1";

        private readonly HashSet<string>? _named;

        private LdapAttributeSelection(bool all, bool none, HashSet<string>? named)
        {
            All = all;
            None = none;
            _named = named;
        }

        public bool All { get; }

        public bool None { get; }

        /// <summary>
        /// Parses the requested attribute list. An EMPTY list means all user attributes (RFC 4511),
        /// which is the opposite of the intuitive reading — treating it as "none" would make the
        /// most common search in the world return bare DNs.
        /// </summary>
        public static LdapAttributeSelection Parse(IReadOnlyList<string> requested)
        {
            if (requested.Count == 0)
                return new LdapAttributeSelection(all: true, none: false, null);

            // 1.1 is only meaningful alone, and a client that sends it alongside names has asked
            // for two incompatible things; honouring the names is the reading that returns less
            // than nothing was requested by, which is the safe direction.
            if (requested.Count == 1 && requested[0] == NoAttributes)
                return new LdapAttributeSelection(all: false, none: true, null);

            var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var all = false;
            foreach (var attribute in requested)
            {
                if (attribute == AllUserAttributes)
                {
                    all = true;
                    continue;
                }
                if (attribute == NoAttributes)
                    continue;

                // An attribute description may carry options (`cn;lang-en`); this directory
                // publishes no subtypes, so the base name is what selects.
                var name = attribute;
                var semicolon = name.IndexOf(';');
                if (semicolon >= 0)
                    name = name[..semicolon];
                if (name.Length > 0)
                    named.Add(name);
            }

            return new LdapAttributeSelection(all, none: false, named);
        }

        /// <summary>Whether an attribute is returned under this selection.</summary>
        public bool Includes(string attribute)
        {
            if (None)
                return false;
            if (All)
                return true;
            return _named is not null && _named.Contains(attribute);
        }
    }

    /// <summary>
    /// Projects one fetched row into the SearchResultEntry the wire carries: its DN and the
    /// attributes the request selected.
    ///
    /// <para><b>The projection is the last egress point, and it is closed the same way as the
    /// others.</b> It iterates the target's DECLARED attribute mappings — never the row's keys —
    /// so a column that the query happened to fetch but the mapping does not publish cannot be
    /// emitted. The credential column is unreachable through this path for the same structural
    /// reason it is unreachable through the DN, the filter, and the subschema: it is not in the
    /// mapping at all, because the mapping parser refuses to publish it.</para>
    /// </summary>
    internal static class LdapEntryProjector
    {
        /// <summary>The synthesized attribute naming a group's members.</summary>
        public const string MemberAttribute = MetadataKeys.Ldap.MemberAttributeName;

        /// <summary>The synthesized reverse attribute naming the groups an entry belongs to.</summary>
        public const string MemberOfAttribute = "memberOf";

        /// <summary>
        /// Builds the entry for <paramref name="row"/>. Returns null when the row has no value in
        /// its naming column — an entry with no name cannot be addressed, and emitting one under a
        /// blank RDN would collide every such row onto a single DN.
        /// </summary>
        public static LdapSearchResultEntry? Project(
            LdapEntryTarget target,
            IReadOnlyDictionary<string, object?> row,
            LdapAttributeSelection selection,
            bool typesOnly,
            IReadOnlyList<string>? memberDns = null,
            IReadOnlyList<string>? memberOfDns = null)
        {
            var namingValue = Render(target, target.NamingColumn, row);
            if (string.IsNullOrEmpty(namingValue))
                return null;

            var attributes = new List<LdapPartialAttribute>();

            if (selection.Includes("objectClass"))
                Add(attributes, "objectClass", target.Config.ObjectClasses, typesOnly);

            // Only the DECLARED mappings are iterated. Walking the row's own keys instead would
            // emit whatever the query fetched, which is how a column added for an internal reason
            // (a sort key, a join key) becomes a published attribute nobody meant to publish.
            foreach (var mapping in target.Attributes)
            {
                if (!selection.Includes(mapping.Attribute))
                    continue;
                var value = Render(target, mapping.Column, row);
                if (value is null)
                    continue; // an absent attribute is omitted, not emitted empty
                Add(attributes, mapping.Attribute, new[] { value }, typesOnly);
            }

            if (memberDns is { Count: > 0 } && selection.Includes(MemberAttribute))
                Add(attributes, MemberAttribute, memberDns, typesOnly);

            if (memberOfDns is { Count: > 0 } && selection.Includes(MemberOfAttribute))
                Add(attributes, MemberOfAttribute, memberOfDns, typesOnly);

            return new LdapSearchResultEntry(target.EntryDn(namingValue), attributes);
        }

        /// <summary>
        /// The database columns a search must fetch for this target: every column its selected
        /// attributes map from, plus the naming column (always — the DN cannot be built without
        /// it) and any column the filter needs to be evaluated exactly.
        ///
        /// <para>The set is derived from the mapping, so it can never name the credential column;
        /// and it is deliberately NOT "every column of the table", because fetching columns the
        /// directory does not publish would put them one projection bug away from the wire.</para>
        /// </summary>
        public static IReadOnlyList<ColumnDto> RequiredColumns(
            LdapEntryTarget target, LdapAttributeSelection selection, LdapFilter filter)
        {
            var columns = new Dictionary<string, ColumnDto>(StringComparer.OrdinalIgnoreCase);

            if (target.Table.ColumnLookup.TryGetValue(target.NamingColumn, out var naming))
                columns[naming.ColumnName] = naming;

            foreach (var mapping in target.Attributes)
            {
                if (!selection.Includes(mapping.Attribute))
                    continue;
                if (target.Table.ColumnLookup.TryGetValue(mapping.Column, out var column))
                    columns[column.ColumnName] = column;
            }

            // The exact evaluation runs over the fetched row, so every attribute the filter
            // mentions must be present even when the client did not ask to see it. Omitting them
            // would make the evaluation read those attributes as absent — silently turning
            // assertions Undefined and dropping entries that do match.
            foreach (var attribute in FilterAttributes(filter))
            {
                if (LdapFilterCompiler.TryResolveColumn(target, attribute, out var column))
                    columns[column.ColumnName] = column;
            }

            // Key columns give the query a deterministic total order, without which paging over
            // it could repeat or skip rows between pages.
            foreach (var key in target.Table.KeyColumns)
                columns[key.ColumnName] = key;

            return columns.Values.ToList();
        }

        /// <summary>Every attribute description a filter tree mentions.</summary>
        public static IEnumerable<string> FilterAttributes(LdapFilter filter)
        {
            switch (filter)
            {
                case LdapFilter.And and:
                    foreach (var child in and.Children)
                        foreach (var name in FilterAttributes(child))
                            yield return name;
                    break;
                case LdapFilter.Or or:
                    foreach (var child in or.Children)
                        foreach (var name in FilterAttributes(child))
                            yield return name;
                    break;
                case LdapFilter.Not not:
                    foreach (var name in FilterAttributes(not.Child))
                        yield return name;
                    break;
                case LdapFilter.Present present:
                    yield return present.Attribute;
                    break;
                case LdapFilter.Comparison comparison:
                    yield return comparison.Attribute;
                    break;
                case LdapFilter.Substrings substrings:
                    yield return substrings.Attribute;
                    break;
            }
        }

        private static void Add(
            List<LdapPartialAttribute> attributes, string type, IReadOnlyList<string> values, bool typesOnly) =>
            attributes.Add(new LdapPartialAttribute(
                type, typesOnly ? Array.Empty<string>() : values));

        // Renders a column's value in its declared LDAP syntax, or null when there is none.
        private static string? Render(
            LdapEntryTarget target, string columnName, IReadOnlyDictionary<string, object?> row)
        {
            if (!row.TryGetValue(columnName, out var value) || value is null or DBNull)
                return null;
            if (!target.Table.ColumnLookup.TryGetValue(columnName, out var column))
                return null;
            return LdapFilterCompiler.RenderValue(column, value);
        }
    }
}
