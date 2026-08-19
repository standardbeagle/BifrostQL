using System;
using System.Collections.Generic;
using System.Linq;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;

namespace BifrostQL.Server.Ldap
{
    /// <summary>
    /// Builds the subschema subentry a PARTICULAR session may read.
    ///
    /// <para><b>Introspection is filtered by the same rule as the data path.</b> A subschema is a
    /// map of what the directory holds; published unfiltered it tells an unauthorized caller the
    /// shape of everything, which is an information-disclosure side channel even though no row
    /// leaves. So the entry is generated from the entry families the session could actually reach,
    /// and it is generated from the SAME <see cref="LdapDirectoryIndex"/> the search path resolves
    /// against — never from a separate, weaker notion of visibility
    /// (protocol-adapter-security invariant 4).</para>
    ///
    /// <para><b>Credential columns are absent structurally.</b> An attribute type reaches the
    /// subschema only by being a published attribute of some mapping, and the mapping parser refuses
    /// to publish the credential column at all. This is the fifth egress path the slice-1 lesson
    /// requires sweeping — attributes list, DN naming, filter match, search projection, and here —
    /// and it is closed by the same single fact rather than by five separate checks.</para>
    /// </summary>
    internal static class LdapDirectorySubschema
    {
        /// <summary>
        /// The subschema entry for <paramref name="session"/>, scoped to what the caller may READ.
        /// An ANONYMOUS session sees only the structural skeleton — the entry exists and is readable
        /// (that is what makes anonymous discovery useful) but names no objectClass and no attribute
        /// type, so it cannot enumerate the directory's shape without binding.
        ///
        /// <para>An AUTHENTICATED session previously received the WHOLE model's subschema, so a
        /// read-denied caller could enumerate the objectClasses and attributeType names of tables
        /// (and columns) it may not read — the introspection side channel invariant 4 forbids. The
        /// subschema is now rebuilt from only the tables/columns this identity may read, using the
        /// SAME <see cref="SchemaReadVisibility"/> projection (the SAME PolicyEvaluator) the query
        /// path enforces, so the discovery surface can never describe more than the data surface.</para>
        /// </summary>
        public static LdapSearchResultEntry ForIdentity(
            LdapDirectoryIndex index, LdapSessionState session, IDbModel model)
        {
            ArgumentNullException.ThrowIfNull(index);
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(model);

            if (session.IsAnonymous || session.UserContext is not { Count: > 0 })
                return LdapDirectoryEntries.Subschema(
                    new LdapSubschema(Array.Empty<string>(), Array.Empty<LdapAttributeType>()));

            // Project the model to the tables (and readable columns) this identity may read, then
            // keep only those that are LDAP-mapped and not hidden — the same mapping filter
            // LdapDirectoryModel.FromModel applies, further narrowed by the caller's read policy.
            var visible = SchemaReadVisibility.Project(model, session.UserContext);
            var mapped = visible
                .Select(vt => (vt.Table, Config: LdapMappingConfig.FromTable(vt.Table), Visible: vt))
                .Where(x => x.Config.IsMapped && !LdapDirectoryModel.IsHidden(x.Table))
                .ToList();

            var visibleColumns = mapped.ToDictionary(
                x => x.Table,
                x => new HashSet<string>(x.Visible.Columns.Select(c => c.ColumnName), StringComparer.OrdinalIgnoreCase));

            var subschema = LdapDirectoryModel.BuildSubschema(
                mapped.Select(x => (x.Table, x.Config)).ToList(),
                isColumnVisible: (table, column) => visibleColumns[table].Contains(column));

            return LdapDirectoryEntries.Subschema(subschema);
        }
    }

    /// <summary>
    /// Applies a search filter to a discovery entry (the RootDSE or the subschema), which is
    /// synthesized rather than read from a table.
    ///
    /// <para>These entries have no row and no mapping, so the ordinary compiler/evaluator pair —
    /// which resolve attribute names through a table's mappings — does not apply. The semantics
    /// still do: an unrecognized attribute is Undefined, and only a TRUE filter returns the entry.
    /// A discovery read that ignored the filter entirely would answer a question the client did not
    /// ask, and would make <c>(objectClass=somethingElse)</c> return the RootDSE.</para>
    /// </summary>
    internal static class LdapDiscoveryFilter
    {
        public static bool Matches(LdapFilter filter, LdapSearchResultEntry entry) =>
            Evaluate(filter, entry) == LdapMatch.True;

        private static LdapMatch Evaluate(LdapFilter filter, LdapSearchResultEntry entry)
        {
            switch (filter)
            {
                case LdapFilter.And and:
                {
                    var undefined = false;
                    foreach (var child in and.Children)
                    {
                        switch (Evaluate(child, entry))
                        {
                            case LdapMatch.False: return LdapMatch.False;
                            case LdapMatch.Undefined: undefined = true; break;
                        }
                    }
                    return undefined ? LdapMatch.Undefined : LdapMatch.True;
                }
                case LdapFilter.Or or:
                {
                    var undefined = false;
                    foreach (var child in or.Children)
                    {
                        switch (Evaluate(child, entry))
                        {
                            case LdapMatch.True: return LdapMatch.True;
                            case LdapMatch.Undefined: undefined = true; break;
                        }
                    }
                    return undefined ? LdapMatch.Undefined : LdapMatch.False;
                }
                case LdapFilter.Not not:
                    return Evaluate(not.Child, entry) switch
                    {
                        LdapMatch.True => LdapMatch.False,
                        LdapMatch.False => LdapMatch.True,
                        _ => LdapMatch.Undefined,
                    };

                case LdapFilter.Present present:
                    return Find(entry, present.Attribute) is not null ? LdapMatch.True : LdapMatch.False;

                case LdapFilter.Comparison comparison:
                {
                    var attribute = Find(entry, comparison.Attribute);
                    if (attribute is null)
                        return LdapMatch.False;
                    if (comparison.Tag is not (LdapProtocol.FilterEqualityMatch or LdapProtocol.FilterApproxMatch))
                        return LdapMatch.Undefined; // ordering has no meaning on these values
                    var asserted = LdapFilterCompiler.Text(comparison.Value);
                    return attribute.Values.Any(v => string.Equals(v, asserted, StringComparison.OrdinalIgnoreCase))
                        ? LdapMatch.True
                        : LdapMatch.False;
                }

                case LdapFilter.Substrings substrings:
                {
                    var attribute = Find(entry, substrings.Attribute);
                    if (attribute is null)
                        return LdapMatch.False;
                    return attribute.Values.Any(v => LdapFilterEvaluator.MatchesPattern(v, substrings))
                        ? LdapMatch.True
                        : LdapMatch.False;
                }

                default:
                    return LdapMatch.Undefined;
            }
        }

        private static LdapPartialAttribute? Find(LdapSearchResultEntry entry, string attribute) =>
            entry.Attributes.FirstOrDefault(a =>
                string.Equals(a.Type, attribute, StringComparison.OrdinalIgnoreCase));
    }
}
