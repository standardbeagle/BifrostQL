using System.Globalization;
using System.Text;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Server.Ldap
{
    /// <summary>
    /// The three-valued result of an LDAP filter item (RFC 4511 §4.5.1). Undefined is NOT a synonym
    /// for FALSE: an entry is returned only when the whole filter is TRUE, but negation propagates
    /// Undefined unchanged, so <c>(!(unknownAttr=x))</c> matches nothing while <c>(!(cn=x))</c>
    /// matches every entry with some other cn. Collapsing Undefined to FALSE early would invert that.
    /// </summary>
    internal enum LdapMatch
    {
        False,
        True,
        Undefined,
    }

    /// <summary>
    /// A filter compiled for pushdown: either a constant, or a predicate to hand the query pipeline.
    ///
    /// <para><b>The pushdown is a SOUND OVER-APPROXIMATION, never the authority.</b> Every entry
    /// whose exact evaluation is TRUE satisfies <see cref="Pushdown"/>, but the converse need not
    /// hold — a filter part that SQL cannot express exactly contributes no constraint at all rather
    /// than a guessed one. Exactness comes from <see cref="LdapFilterEvaluator"/>, which re-evaluates
    /// the original filter against each projected entry. Splitting the two is what lets the compiler
    /// stay conservative: the worst a missing constraint can do is fetch rows that are then dropped,
    /// whereas a wrong constraint would silently return the wrong entries.</para>
    ///
    /// <para>The pushdown is an OPTIMIZATION and a narrowing only. It never widens what the query
    /// may see: tenant, policy, and soft-delete predicates are applied by the pipeline on top of it
    /// and cannot be displaced by anything compiled here.</para>
    /// </summary>
    internal sealed record LdapCompiledFilter(LdapMatch? Constant, IReadOnlyDictionary<string, object?>? Pushdown)
    {
        /// <summary>Nothing can match — the search can answer without executing anything.</summary>
        public static readonly LdapCompiledFilter Never = new(LdapMatch.False, null);

        /// <summary>No constraint expressible; every candidate row is fetched and evaluated exactly.</summary>
        public static readonly LdapCompiledFilter Unconstrained = new(null, null);

        public static LdapCompiledFilter Predicate(IReadOnlyDictionary<string, object?> node) => new(null, node);

        public bool MatchesNothing => Constant == LdapMatch.False;
    }

    /// <summary>
    /// Compiles a decoded LDAP filter into a pipeline predicate for ONE entry family.
    ///
    /// <para><b>Every name comes from the mapping.</b> An attribute is resolved through the table's
    /// declared <c>ldap-attributes</c> mapping and nowhere else, so a filter can only ever reference
    /// a column the configuration published. An unmapped name is Undefined (RFC 4511's "attribute
    /// type not recognized"), which matches nothing — it is never passed through as a column name,
    /// so there is no path by which client text becomes an identifier. The credential column is
    /// unreachable for the same reason: the mapping parser refuses to publish it, so no attribute
    /// name resolves to it.</para>
    ///
    /// <para><b>Every value becomes a parameter.</b> Assertion values are raw octets from the wire.
    /// They are decoded to a typed value for the column's syntax and placed in the predicate as
    /// VALUES; nothing is concatenated into SQL. A value that does not conform to the attribute's
    /// syntax makes the assertion Undefined rather than being coerced — a coerced value would match
    /// rows the client did not ask for.</para>
    ///
    /// <para><b>Substring assertions never build a LIKE pattern.</b> Only the wildcard operator
    /// family (<c>_contains</c>/<c>_starts_with</c>/<c>_ends_with</c>) is emitted, because it escapes
    /// the bound value and declares an ESCAPE clause. The raw <c>_like</c> operator passes its
    /// pattern through untouched, which on client-supplied text means the client chooses the
    /// wildcards — a <c>%</c> in a fragment would match the whole table. Multi-fragment ordering
    /// that the wildcard family cannot express is enforced afterwards by the evaluator, in memory,
    /// with no pattern language involved at all.</para>
    /// </summary>
    internal static class LdapFilterCompiler
    {
        /// <summary>The virtual attribute every entry carries, answered from the mapping's declared classes.</summary>
        public const string ObjectClassAttribute = "objectClass";

        /// <summary>Compiles <paramref name="filter"/> for <paramref name="target"/>.</summary>
        public static LdapCompiledFilter Compile(LdapFilter filter, LdapEntryTarget target)
        {
            ArgumentNullException.ThrowIfNull(filter);
            ArgumentNullException.ThrowIfNull(target);
            return CompileNode(filter, target, negated: false);
        }

        private static LdapCompiledFilter CompileNode(LdapFilter filter, LdapEntryTarget target, bool negated)
        {
            switch (filter)
            {
                // De Morgan: pushing the negation down to the leaves is what lets a NOT compile at
                // all, since the predicate language has no negation node of its own.
                case LdapFilter.Not not:
                    return CompileNode(not.Child, target, !negated);

                case LdapFilter.And and:
                    return negated
                        ? CompileDisjunction(and.Children, target, negated: true)
                        : CompileConjunction(and.Children, target, negated: false);

                case LdapFilter.Or or:
                    return negated
                        ? CompileConjunction(or.Children, target, negated: true)
                        : CompileDisjunction(or.Children, target, negated: false);

                case LdapFilter.Present present:
                    return CompilePresent(present, target, negated);

                case LdapFilter.Comparison comparison:
                    return CompileComparison(comparison, target, negated);

                case LdapFilter.Substrings substrings:
                    return CompileSubstrings(substrings, target, negated);

                default:
                    // extensibleMatch and anything else the codec does not model: Undefined, which
                    // constrains nothing and matches nothing once evaluated.
                    return LdapCompiledFilter.Unconstrained;
            }
        }

        // AND: intersect what each child expresses. A child that expresses nothing simply drops out
        // of the conjunction — dropping a constraint keeps the result a superset, which is sound.
        // A child that can never match short-circuits the whole conjunction.
        private static LdapCompiledFilter CompileConjunction(
            IReadOnlyList<LdapFilter> children, LdapEntryTarget target, bool negated)
        {
            // An empty AND is TRUE by definition (RFC 4511 §4.5.1); its negation is FALSE.
            if (children.Count == 0)
                return negated ? LdapCompiledFilter.Never : LdapCompiledFilter.Unconstrained;

            var terms = new List<object>();
            foreach (var child in children)
            {
                var compiled = CompileNode(child, target, negated);
                if (compiled.MatchesNothing)
                    return LdapCompiledFilter.Never;
                if (compiled.Pushdown is { } node)
                    terms.Add(node);
            }

            return terms.Count switch
            {
                0 => LdapCompiledFilter.Unconstrained,
                1 => LdapCompiledFilter.Predicate((IReadOnlyDictionary<string, object?>)terms[0]),
                _ => LdapCompiledFilter.Predicate(new Dictionary<string, object?> { ["and"] = terms }),
            };
        }

        // OR: every branch must be expressible, or the disjunction as a whole is unconstrained. This
        // is the asymmetry that makes the pushdown sound -- dropping ONE branch of an OR would
        // narrow the result and hide entries that genuinely match, so it is never done.
        private static LdapCompiledFilter CompileDisjunction(
            IReadOnlyList<LdapFilter> children, LdapEntryTarget target, bool negated)
        {
            // An empty OR is FALSE by definition; its negation is TRUE.
            if (children.Count == 0)
                return negated ? LdapCompiledFilter.Unconstrained : LdapCompiledFilter.Never;

            var terms = new List<object>();
            foreach (var child in children)
            {
                var compiled = CompileNode(child, target, negated);
                if (compiled.MatchesNothing)
                    continue; // a branch that can never match contributes nothing to an OR
                if (compiled.Pushdown is not { } node)
                    return LdapCompiledFilter.Unconstrained;
                terms.Add(node);
            }

            return terms.Count switch
            {
                0 => LdapCompiledFilter.Never,
                1 => LdapCompiledFilter.Predicate((IReadOnlyDictionary<string, object?>)terms[0]),
                _ => LdapCompiledFilter.Predicate(new Dictionary<string, object?> { ["or"] = terms }),
            };
        }

        private static LdapCompiledFilter CompilePresent(
            LdapFilter.Present present, LdapEntryTarget target, bool negated)
        {
            // Every entry has objectClasses, so presence is a constant for the family.
            if (IsObjectClass(present.Attribute))
                return negated ? LdapCompiledFilter.Never : LdapCompiledFilter.Unconstrained;

            if (!TryResolveColumn(target, present.Attribute, out var column))
                return LdapCompiledFilter.Unconstrained; // Undefined: constrains nothing

            // Presence is "the column is not null". Both directions are exact, so both push down.
            return LdapCompiledFilter.Predicate(Leaf(
                column, negated ? FilterOperators.Eq : FilterOperators.Neq, null));
        }

        private static LdapCompiledFilter CompileComparison(
            LdapFilter.Comparison comparison, LdapEntryTarget target, bool negated)
        {
            if (IsObjectClass(comparison.Attribute))
            {
                var declared = comparison.Tag == LdapProtocol.FilterEqualityMatch
                               && DeclaresObjectClass(target, Text(comparison.Value));
                return declared != negated ? LdapCompiledFilter.Unconstrained : LdapCompiledFilter.Never;
            }

            if (!TryResolveColumn(target, comparison.Attribute, out var column))
                return LdapCompiledFilter.Unconstrained;

            // A value that does not conform to the column's syntax makes the assertion Undefined.
            // Coercing it instead would compare against something the client never asserted.
            if (!TryTypedValue(column, comparison.Value, out var value))
                return LdapCompiledFilter.Unconstrained;

            // Each operator's negation is another exact operator, so both directions push down.
            // SQL's NULL semantics match LDAP's here: `col <> v` excludes NULL rows, and an absent
            // attribute makes the assertion Undefined, which is likewise not returned.
            var op = comparison.Tag switch
            {
                LdapProtocol.FilterEqualityMatch => negated ? FilterOperators.Neq : FilterOperators.Eq,
                // approxMatch has no separate semantics in this directory; RFC 4511 permits a server
                // to treat it as equality, and inventing a fuzzy match would return entries the
                // client did not ask for.
                LdapProtocol.FilterApproxMatch => negated ? FilterOperators.Neq : FilterOperators.Eq,
                LdapProtocol.FilterGreaterOrEqual => negated ? FilterOperators.Lt : FilterOperators.Gte,
                LdapProtocol.FilterLessOrEqual => negated ? FilterOperators.Gt : FilterOperators.Lte,
                _ => null,
            };
            if (op is null)
                return LdapCompiledFilter.Unconstrained;

            return LdapCompiledFilter.Predicate(Leaf(column, op, value));
        }

        private static LdapCompiledFilter CompileSubstrings(
            LdapFilter.Substrings substrings, LdapEntryTarget target, bool negated)
        {
            // objectClass is a synthesized multi-valued attribute with no column to scan.
            if (IsObjectClass(substrings.Attribute))
                return LdapCompiledFilter.Unconstrained;

            if (!TryResolveColumn(target, substrings.Attribute, out var column))
                return LdapCompiledFilter.Unconstrained;

            // A substring assertion only applies to text. On a non-text column it is Undefined
            // rather than a string comparison against a coerced value.
            if (LdapMappingConfig.ColumnSyntax(column.DataType) != LdapSyntax.DirectoryString)
                return LdapCompiledFilter.Unconstrained;

            // Negated substrings do not push down: NOT of an over-approximation is an
            // under-approximation, which would hide entries that genuinely match. The evaluator
            // still applies the assertion exactly.
            if (negated)
                return LdapCompiledFilter.Unconstrained;

            var terms = new List<object>();
            if (substrings.Initial is { } initial)
                terms.Add(Leaf(column, FilterOperators.StartsWith, Text(initial)));
            foreach (var fragment in substrings.Any)
                terms.Add(Leaf(column, FilterOperators.Contains, Text(fragment)));
            if (substrings.Final is { } final)
                terms.Add(Leaf(column, FilterOperators.EndsWith, Text(final)));

            return terms.Count switch
            {
                0 => LdapCompiledFilter.Unconstrained,
                1 => LdapCompiledFilter.Predicate((IReadOnlyDictionary<string, object?>)terms[0]),
                // An AND of the individual fragments is a necessary condition for the ordered
                // pattern, so it is a sound superset. The evaluator enforces the ORDER.
                _ => LdapCompiledFilter.Predicate(new Dictionary<string, object?> { ["and"] = terms }),
            };
        }

        // ---- shared helpers, used by the evaluator too ----

        /// <summary>
        /// Resolves an attribute name to the column the mapping publishes it from. This is the ONLY
        /// route from a client-supplied name to a column, and it consults only the declared mapping —
        /// so an unmapped name (including the credential column's own name, which is never mapped)
        /// resolves to nothing.
        /// </summary>
        public static bool TryResolveColumn(LdapEntryTarget target, string attribute, out ColumnDto column)
        {
            column = null!;
            // An attribute description may carry options (`cn;lang-en`); the options select a
            // subtype this directory does not publish, so the base name is what resolves.
            var name = attribute;
            var semicolon = name.IndexOf(';');
            if (semicolon >= 0)
                name = name[..semicolon];

            foreach (var mapping in target.Attributes)
            {
                if (!string.Equals(mapping.Attribute, name, StringComparison.OrdinalIgnoreCase))
                    continue;
                return target.Table.ColumnLookup.TryGetValue(mapping.Column, out column!);
            }
            return false;
        }

        public static bool IsObjectClass(string attribute) =>
            string.Equals(attribute, ObjectClassAttribute, StringComparison.OrdinalIgnoreCase);

        public static bool DeclaresObjectClass(LdapEntryTarget target, string value) =>
            target.Config.ObjectClasses.Any(c => string.Equals(c, value, StringComparison.OrdinalIgnoreCase));

        /// <summary>Decodes an assertion value's octets as UTF-8 text (LDAP's DirectoryString wire form).</summary>
        public static string Text(byte[] value) => Encoding.UTF8.GetString(value);

        /// <summary>
        /// Converts an assertion value's octets to a value comparable against
        /// <paramref name="column"/>. Returns false when the text does not conform to the column's
        /// syntax — the assertion is then Undefined, never a coerced comparison.
        ///
        /// <para>The catch covers the whole BCL parse family, not just the obviously-malformed case:
        /// a well-formed but out-of-range integer throws <see cref="OverflowException"/> rather than
        /// <see cref="FormatException"/>, and on a wire-facing path a narrower catch is a
        /// fail-open waiting on a boundary value (protocol-adapter-security invariant 5).</para>
        /// </summary>
        public static bool TryTypedValue(ColumnDto column, byte[] octets, out object? value)
        {
            var text = Text(octets);
            value = text;
            try
            {
                switch (LdapMappingConfig.ColumnSyntax(column.DataType))
                {
                    case LdapSyntax.Integer:
                        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                            return false;
                        value = number;
                        return true;

                    case LdapSyntax.Boolean:
                        if (string.Equals(text, "TRUE", StringComparison.OrdinalIgnoreCase)) value = true;
                        else if (string.Equals(text, "FALSE", StringComparison.OrdinalIgnoreCase)) value = false;
                        else return false;
                        return true;

                    case LdapSyntax.GeneralizedTime:
                        if (!TryParseGeneralizedTime(text, out var timestamp))
                            return false;
                        value = timestamp;
                        return true;

                    case LdapSyntax.OctetString:
                        // A binary column has no meaningful text assertion; comparing its bytes
                        // against a UTF-8 rendering would match by accident, if at all.
                        return false;

                    default:
                        return true;
                }
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
            {
                return false;
            }
        }

        /// <summary>
        /// Parses an LDAP GeneralizedTime (RFC 4517 §3.3.13), <c>YYYYMMDDHHMMSS[.f]Z</c>, and also
        /// accepts an ISO-8601 rendering, which is what a client library that never learned the LDAP
        /// form will send.
        /// </summary>
        public static bool TryParseGeneralizedTime(string text, out DateTime value)
        {
            var formats = new[]
            {
                "yyyyMMddHHmmss'Z'", "yyyyMMddHHmmss.fff'Z'", "yyyyMMddHHmm'Z'", "yyyyMMddHH'Z'",
                "yyyyMMddHHmmss", "yyyyMMdd",
            };
            if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out value))
                return true;

            return DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out value);
        }

        /// <summary>Renders a column value in the LDAP wire form its syntax declares.</summary>
        public static string RenderValue(ColumnDto column, object value) =>
            LdapMappingConfig.ColumnSyntax(column.DataType) switch
            {
                LdapSyntax.Boolean => value is bool b
                    ? (b ? "TRUE" : "FALSE")
                    : (Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
                LdapSyntax.GeneralizedTime => value is DateTime time
                    ? time.ToUniversalTime().ToString("yyyyMMddHHmmss'Z'", CultureInfo.InvariantCulture)
                    : (Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
                LdapSyntax.OctetString => value is byte[] bytes
                    ? Convert.ToBase64String(bytes)
                    : (Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
            };

        // A single predicate leaf, keyed by the column's GraphQL name because that is the name
        // TableFilter.FromObject resolves against.
        private static Dictionary<string, object?> Leaf(ColumnDto column, string op, object? value) =>
            new() { [column.GraphQlName] = new Dictionary<string, object?> { [op] = value } };
    }
}
