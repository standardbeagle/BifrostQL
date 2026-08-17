using System.Globalization;
using BifrostQL.Core.Model;

namespace BifrostQL.Server.Ldap
{
    /// <summary>
    /// Evaluates a decoded LDAP filter EXACTLY against one candidate entry, in memory, with RFC 4511
    /// §4.5.1 three-valued logic. This is the authority on whether an entry is returned;
    /// <see cref="LdapFilterCompiler"/> only narrows what is fetched.
    ///
    /// <para><b>Why the exact pass exists.</b> Two things SQL cannot express faithfully: the ORDER
    /// of a multi-fragment substring assertion, and the difference between FALSE and Undefined under
    /// negation. Approximating either in the predicate would silently return entries the filter does
    /// not name. Doing the exact match here costs one pass over the page that was already fetched,
    /// and it needs no pattern language — the fragments are compared as literal spans, so a
    /// <c>%</c>, <c>_</c>, or <c>*</c> inside a client's fragment is just a character.</para>
    ///
    /// <para><b>What it can see.</b> Only the values already projected for the entry, which are only
    /// the columns the mapping publishes. The credential column is not among them (the mapping
    /// parser refuses to publish it), so no filter can test it — including indirectly, since an
    /// unresolvable attribute is Undefined rather than an error that would distinguish it from any
    /// other unmapped name.</para>
    /// </summary>
    internal static class LdapFilterEvaluator
    {
        /// <summary>
        /// Evaluates <paramref name="filter"/> against a candidate entry's column values (keyed by
        /// database column name, as the query pipeline returns them).
        /// </summary>
        public static LdapMatch Evaluate(
            LdapFilter filter, LdapEntryTarget target, IReadOnlyDictionary<string, object?> row)
        {
            ArgumentNullException.ThrowIfNull(filter);
            ArgumentNullException.ThrowIfNull(target);
            ArgumentNullException.ThrowIfNull(row);
            return Node(filter, target, row);
        }

        /// <summary>Whether an entry is RETURNED: only a TRUE filter qualifies, never Undefined.</summary>
        public static bool Matches(
            LdapFilter filter, LdapEntryTarget target, IReadOnlyDictionary<string, object?> row) =>
            Evaluate(filter, target, row) == LdapMatch.True;

        private static LdapMatch Node(
            LdapFilter filter, LdapEntryTarget target, IReadOnlyDictionary<string, object?> row) =>
            filter switch
            {
                LdapFilter.And and => And(and.Children, target, row),
                LdapFilter.Or or => Or(or.Children, target, row),
                LdapFilter.Not not => Not(Node(not.Child, target, row)),
                LdapFilter.Present present => Present(present, target, row),
                LdapFilter.Comparison comparison => Comparison(comparison, target, row),
                LdapFilter.Substrings substrings => Substrings(substrings, target, row),
                // extensibleMatch and any unmodelled filter type: the server does not implement the
                // matching rule, which RFC 4511 makes Undefined.
                _ => LdapMatch.Undefined,
            };

        // FALSE dominates; otherwise any Undefined makes the conjunction Undefined. An empty AND is TRUE.
        private static LdapMatch And(
            IReadOnlyList<LdapFilter> children, LdapEntryTarget target, IReadOnlyDictionary<string, object?> row)
        {
            var undefined = false;
            foreach (var child in children)
            {
                switch (Node(child, target, row))
                {
                    case LdapMatch.False: return LdapMatch.False;
                    case LdapMatch.Undefined: undefined = true; break;
                }
            }
            return undefined ? LdapMatch.Undefined : LdapMatch.True;
        }

        // TRUE dominates; otherwise any Undefined makes the disjunction Undefined. An empty OR is FALSE.
        private static LdapMatch Or(
            IReadOnlyList<LdapFilter> children, LdapEntryTarget target, IReadOnlyDictionary<string, object?> row)
        {
            var undefined = false;
            foreach (var child in children)
            {
                switch (Node(child, target, row))
                {
                    case LdapMatch.True: return LdapMatch.True;
                    case LdapMatch.Undefined: undefined = true; break;
                }
            }
            return undefined ? LdapMatch.Undefined : LdapMatch.False;
        }

        // Undefined stays Undefined under negation. Folding it to TRUE here is the classic way a
        // filter over an unknown attribute starts returning the whole directory.
        private static LdapMatch Not(LdapMatch inner) => inner switch
        {
            LdapMatch.True => LdapMatch.False,
            LdapMatch.False => LdapMatch.True,
            _ => LdapMatch.Undefined,
        };

        private static LdapMatch Present(
            LdapFilter.Present present, LdapEntryTarget target, IReadOnlyDictionary<string, object?> row)
        {
            if (LdapFilterCompiler.IsObjectClass(present.Attribute))
                return LdapMatch.True; // every entry declares at least one objectClass

            if (!LdapFilterCompiler.TryResolveColumn(target, present.Attribute, out var column))
                return LdapMatch.Undefined; // attribute type not recognized

            // Present is FALSE (not Undefined) for a recognized attribute with no value, so
            // (!(mail=*)) correctly returns the entries that have no mail.
            return Value(row, column) is null ? LdapMatch.False : LdapMatch.True;
        }

        private static LdapMatch Comparison(
            LdapFilter.Comparison comparison, LdapEntryTarget target, IReadOnlyDictionary<string, object?> row)
        {
            if (LdapFilterCompiler.IsObjectClass(comparison.Attribute))
            {
                if (comparison.Tag is not (LdapProtocol.FilterEqualityMatch or LdapProtocol.FilterApproxMatch))
                    return LdapMatch.Undefined; // ordering on objectClass has no defined meaning
                return LdapFilterCompiler.DeclaresObjectClass(target, LdapFilterCompiler.Text(comparison.Value))
                    ? LdapMatch.True
                    : LdapMatch.False;
            }

            if (!LdapFilterCompiler.TryResolveColumn(target, comparison.Attribute, out var column))
                return LdapMatch.Undefined;

            var stored = Value(row, column);
            if (stored is null)
                return LdapMatch.Undefined; // no value to assert against

            if (!LdapFilterCompiler.TryTypedValue(column, comparison.Value, out var asserted))
                return LdapMatch.Undefined; // the value does not conform to the attribute's syntax

            return comparison.Tag switch
            {
                LdapProtocol.FilterEqualityMatch or LdapProtocol.FilterApproxMatch =>
                    From(AreEqual(column, stored, asserted)),
                LdapProtocol.FilterGreaterOrEqual => CompareTo(column, stored, asserted, atLeast: true),
                LdapProtocol.FilterLessOrEqual => CompareTo(column, stored, asserted, atLeast: false),
                _ => LdapMatch.Undefined,
            };
        }

        private static LdapMatch Substrings(
            LdapFilter.Substrings substrings, LdapEntryTarget target, IReadOnlyDictionary<string, object?> row)
        {
            if (LdapFilterCompiler.IsObjectClass(substrings.Attribute))
            {
                foreach (var objectClass in target.Config.ObjectClasses)
                {
                    if (MatchesPattern(objectClass, substrings))
                        return LdapMatch.True;
                }
                return LdapMatch.False;
            }

            if (!LdapFilterCompiler.TryResolveColumn(target, substrings.Attribute, out var column))
                return LdapMatch.Undefined;

            // A substring assertion is defined only for text syntaxes.
            if (LdapMappingConfig.ColumnSyntax(column.DataType) != LdapSyntax.DirectoryString)
                return LdapMatch.Undefined;

            var stored = Value(row, column);
            if (stored is null)
                return LdapMatch.Undefined;

            return From(MatchesPattern(
                Convert.ToString(stored, CultureInfo.InvariantCulture) ?? string.Empty, substrings));
        }

        /// <summary>
        /// The exact substring match: <c>initial</c> anchors the start, each <c>any</c> fragment
        /// appears after the previous one, and <c>final</c> anchors the end without overlapping what
        /// has already been consumed.
        ///
        /// <para>Fragments are compared as LITERAL spans with <see cref="StringComparison.OrdinalIgnoreCase"/>
        /// — the caseIgnoreSubstringsMatch rule DirectoryString attributes use. There is no pattern
        /// language and no regex, so a client fragment containing <c>*</c>, <c>%</c>, <c>_</c>, or a
        /// backslash is matched as those characters, and no fragment can be crafted into
        /// catastrophic backtracking.</para>
        /// </summary>
        public static bool MatchesPattern(string value, LdapFilter.Substrings substrings)
        {
            var cursor = 0;

            if (substrings.Initial is { } initial)
            {
                var text = LdapFilterCompiler.Text(initial);
                if (!value.StartsWith(text, StringComparison.OrdinalIgnoreCase))
                    return false;
                cursor = text.Length;
            }

            foreach (var fragment in substrings.Any)
            {
                var text = LdapFilterCompiler.Text(fragment);
                if (text.Length == 0)
                    continue;
                var found = value.IndexOf(text, cursor, StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                    return false;
                cursor = found + text.Length;
            }

            if (substrings.Final is { } final)
            {
                var text = LdapFilterCompiler.Text(final);
                // The final anchor must not overlap what the earlier fragments already consumed,
                // or "abc" would satisfy (cn=ab*bc) by reusing the same 'b'.
                if (value.Length - text.Length < cursor)
                    return false;
                if (!value.EndsWith(text, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        private static object? Value(IReadOnlyDictionary<string, object?> row, ColumnDto column)
        {
            if (row.TryGetValue(column.ColumnName, out var value) && value is not DBNull)
                return value;
            return null;
        }

        private static bool AreEqual(ColumnDto column, object stored, object? asserted)
        {
            if (asserted is null)
                return false;

            return LdapMappingConfig.ColumnSyntax(column.DataType) switch
            {
                // caseIgnoreMatch is the equality rule for the DirectoryString syntaxes here.
                LdapSyntax.DirectoryString => string.Equals(
                    Convert.ToString(stored, CultureInfo.InvariantCulture),
                    Convert.ToString(asserted, CultureInfo.InvariantCulture),
                    StringComparison.OrdinalIgnoreCase),
                _ => Compare(column, stored, asserted) == 0,
            };
        }

        private static LdapMatch CompareTo(ColumnDto column, object stored, object? asserted, bool atLeast)
        {
            if (asserted is null)
                return LdapMatch.Undefined;
            var order = Compare(column, stored, asserted);
            if (order is null)
                return LdapMatch.Undefined;
            return From(atLeast ? order >= 0 : order <= 0);
        }

        // Orders a stored value against an asserted one in the column's own syntax. Returns null
        // when the two are not comparable — Undefined, never a coincidental string ordering.
        private static int? Compare(ColumnDto column, object stored, object asserted)
        {
            try
            {
                switch (LdapMappingConfig.ColumnSyntax(column.DataType))
                {
                    case LdapSyntax.Integer:
                        return Convert.ToDecimal(stored, CultureInfo.InvariantCulture)
                            .CompareTo(Convert.ToDecimal(asserted, CultureInfo.InvariantCulture));

                    case LdapSyntax.GeneralizedTime:
                        return Convert.ToDateTime(stored, CultureInfo.InvariantCulture)
                            .CompareTo(Convert.ToDateTime(asserted, CultureInfo.InvariantCulture));

                    case LdapSyntax.Boolean:
                        return Convert.ToBoolean(stored, CultureInfo.InvariantCulture)
                            .CompareTo(Convert.ToBoolean(asserted, CultureInfo.InvariantCulture));

                    case LdapSyntax.OctetString:
                        return null;

                    default:
                        return string.Compare(
                            Convert.ToString(stored, CultureInfo.InvariantCulture),
                            Convert.ToString(asserted, CultureInfo.InvariantCulture),
                            StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex) when (ex is FormatException or OverflowException
                                          or ArgumentException or InvalidCastException)
            {
                // A stored value that will not convert is not comparable. This is a data shape the
                // wire can reach, so the catch spans the full conversion family rather than the one
                // obvious case (protocol-adapter-security invariant 5).
                return null;
            }
        }

        private static LdapMatch From(bool value) => value ? LdapMatch.True : LdapMatch.False;
    }
}
