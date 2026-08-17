using System.Text;

namespace BifrostQL.Server.Ldap
{
    /// <summary>One component of a distinguished name: its attribute type and its unescaped value.</summary>
    internal readonly record struct LdapRdn(string Attribute, string Value);

    /// <summary>
    /// Distinguished-name syntax (RFC 4514): splitting a DN into components, unescaping a
    /// component value, escaping one back, and reducing a DN to a canonical form for comparison.
    ///
    /// <para><b>Why escaping is load-bearing here.</b> An entry's DN is built from a COLUMN VALUE.
    /// A value containing a comma, a plus, or a backslash would, unescaped, split into extra DN
    /// components — so <c>cn=Doe, John</c> would name an entry whose parent container is
    /// <c>John</c>, and two different rows could produce the same DN text. Escaping on the way out
    /// and unescaping on the way in is what makes the DN a faithful, injective rendering of the
    /// value rather than a string that happens to look right for well-behaved data.</para>
    ///
    /// <para><b>Comparison.</b> LDAP matches DNs by matching rule, not by byte equality: component
    /// order is significant, but attribute names and (for the DirectoryString syntaxes this
    /// directory publishes) values match case-insensitively, and whitespace around the separators
    /// is not significant. <see cref="CanonicalKey"/> reduces a DN to the one string those rules
    /// make equal, so every DN comparison in the adapter goes through a single definition and two
    /// spellings of the same DN can never resolve differently.</para>
    /// </summary>
    internal static class LdapDn
    {
        /// <summary>Characters RFC 4514 §2.4 requires be escaped anywhere in a component value.</summary>
        private const string AlwaysEscaped = "\",+;<>\\=";

        /// <summary>
        /// Splits a DN into its components, unescaping each value. Returns false for any input that
        /// is not a well-formed DN — an empty component, a component with no <c>=</c>, an unknown
        /// attribute-type syntax, or a trailing escape. A malformed DN is never partially accepted:
        /// the caller treats it exactly as it treats a DN naming nothing, so a client cannot learn
        /// anything from the difference.
        /// </summary>
        public static bool TryParse(string dn, out IReadOnlyList<LdapRdn> components)
        {
            components = Array.Empty<LdapRdn>();
            if (dn.Length == 0)
                return true; // the empty DN — the RootDSE — parses to no components

            var parsed = new List<LdapRdn>();
            foreach (var component in SplitUnescaped(dn, ','))
            {
                var text = TrimUnescaped(component);
                if (text.Length == 0)
                    return false;

                // The '=' that separates type from value must itself be unescaped.
                var separator = IndexOfUnescaped(text, '=');
                if (separator <= 0)
                    return false;

                var attribute = text[..separator].Trim();
                if (!IsAttributeType(attribute))
                    return false;

                if (!TryUnescape(text[(separator + 1)..], out var value))
                    return false;

                parsed.Add(new LdapRdn(attribute, value));
            }

            components = parsed;
            return true;
        }

        /// <summary>Renders components back to DN text, escaping each value per RFC 4514 §2.4.</summary>
        public static string Format(IEnumerable<LdapRdn> components) =>
            string.Join(",", components.Select(c => $"{c.Attribute}={Escape(c.Value)}"));

        /// <summary>Renders a single component (<c>attr=escaped-value</c>).</summary>
        public static string FormatRdn(string attribute, string value) => $"{attribute}={Escape(value)}";

        /// <summary>
        /// Escapes a component value per RFC 4514 §2.4: the always-special characters, a leading
        /// <c>#</c> or space, and a trailing space. A NUL is escaped in hex form; everything else
        /// is escaped with a backslash, which keeps the output readable for ordinary values.
        /// </summary>
        public static string Escape(string value)
        {
            if (value.Length == 0)
                return string.Empty;

            var builder = new StringBuilder(value.Length + 8);
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                var leading = i == 0;
                var trailing = i == value.Length - 1;

                if (c == '\0')
                    builder.Append("\\00");
                else if (AlwaysEscaped.Contains(c)
                         || (leading && (c == '#' || c == ' '))
                         || (trailing && c == ' '))
                    builder.Append('\\').Append(c);
                else
                    builder.Append(c);
            }
            return builder.ToString();
        }

        /// <summary>
        /// Unescapes a component value. Both RFC 4514 forms are accepted: <c>\c</c> for a literal
        /// character and <c>\XX</c> for a hex-encoded octet. A dangling or malformed escape makes
        /// the whole DN invalid rather than being dropped — silently discarding it would let two
        /// distinct inputs unescape to the same value.
        /// </summary>
        public static bool TryUnescape(string value, out string unescaped)
        {
            unescaped = string.Empty;
            if (value.IndexOf('\\') < 0)
            {
                unescaped = value.Trim();
                return true;
            }

            var builder = new StringBuilder(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }
                if (i + 1 >= value.Length)
                    return false; // dangling escape

                var next = value[i + 1];
                if (IsHex(next))
                {
                    if (i + 2 >= value.Length || !IsHex(value[i + 2]))
                        return false; // half a hex escape
                    builder.Append((char)Convert.ToInt32(value.Substring(i + 1, 2), 16));
                    i += 2;
                }
                else
                {
                    builder.Append(next);
                    i += 1;
                }
            }
            unescaped = builder.ToString();
            return true;
        }

        /// <summary>
        /// The canonical comparison form of a DN: attribute types and values lower-cased,
        /// separators normalized, values re-escaped from their unescaped form. Two DNs are the same
        /// entry name exactly when their canonical keys are equal. Returns null for a DN that does
        /// not parse — an unparseable DN is never equal to anything, including itself.
        /// </summary>
        public static string? CanonicalKey(string dn)
        {
            if (!TryParse(dn, out var components))
                return null;
            return string.Join(",", components.Select(c =>
                $"{c.Attribute.ToLowerInvariant()}={Escape(c.Value).ToLowerInvariant()}"));
        }

        /// <summary>The canonical key of components already parsed (no re-parse, same definition).</summary>
        public static string CanonicalKey(IEnumerable<LdapRdn> components) =>
            string.Join(",", components.Select(c =>
                $"{c.Attribute.ToLowerInvariant()}={Escape(c.Value).ToLowerInvariant()}"));

        /// <summary>
        /// Whether <paramref name="candidateKey"/> names an entry strictly below
        /// <paramref name="ancestorKey"/> in the tree. Both arguments are canonical keys. The
        /// comparison is on a COMPONENT boundary — a suffix match on raw text would make
        /// <c>ou=people,dc=evil,dc=com</c> look like a descendant of <c>dc=com</c>'s sibling
        /// <c>c,dc=com</c>, which is how suffix checks become scope escapes.
        /// </summary>
        public static bool IsDescendantOf(string candidateKey, string ancestorKey)
        {
            if (ancestorKey.Length == 0)
                return candidateKey.Length > 0; // everything is below the root
            if (candidateKey.Length <= ancestorKey.Length)
                return false;
            return candidateKey.EndsWith(ancestorKey, StringComparison.Ordinal)
                && candidateKey[candidateKey.Length - ancestorKey.Length - 1] == ',';
        }

        /// <summary>Whether a candidate is the ancestor's IMMEDIATE child (exactly one component deeper).</summary>
        public static bool IsChildOf(string candidateKey, string ancestorKey)
        {
            if (!IsDescendantOf(candidateKey, ancestorKey))
                return false;
            var prefixLength = ancestorKey.Length == 0
                ? candidateKey.Length
                : candidateKey.Length - ancestorKey.Length - 1;
            return IndexOfUnescaped(candidateKey[..prefixLength], ',') < 0;
        }

        /// <summary>Splits on an UNESCAPED separator, so an escaped one stays inside its component.</summary>
        public static List<string> SplitUnescaped(string text, char separator)
        {
            var parts = new List<string>();
            var start = 0;
            var escaped = false;
            for (var i = 0; i < text.Length; i++)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (text[i] == '\\')
                    escaped = true;
                else if (text[i] == separator)
                {
                    parts.Add(text[start..i]);
                    start = i + 1;
                }
            }
            parts.Add(text[start..]);
            return parts;
        }

        /// <summary>
        /// Trims the insignificant whitespace around a DN component. A trailing space is
        /// insignificant only when it is UNESCAPED: <c>cn=trailing\ </c> deliberately names a value
        /// that ends in a space, and a plain <c>Trim()</c> would eat that space and leave the
        /// backslash dangling — turning a legal DN into an unparseable one, so the entry could
        /// never be addressed by its own DN.
        /// </summary>
        private static string TrimUnescaped(string text)
        {
            var start = 0;
            while (start < text.Length && text[start] == ' ')
                start++;

            var end = text.Length;
            while (end > start && text[end - 1] == ' ' && !IsEscapedPosition(text, end - 1))
                end--;

            return text[start..end];
        }

        // Whether the character at <paramref name="index"/> is preceded by an ODD number of
        // backslashes, i.e. is itself escaped. Counting is required: "\\\\ " ends in an escaped
        // backslash followed by a genuinely insignificant space.
        private static bool IsEscapedPosition(string text, int index)
        {
            var backslashes = 0;
            for (var i = index - 1; i >= 0 && text[i] == '\\'; i--)
                backslashes++;
            return backslashes % 2 == 1;
        }

        private static int IndexOfUnescaped(string text, char target)
        {
            var escaped = false;
            for (var i = 0; i < text.Length; i++)
            {
                if (escaped) { escaped = false; continue; }
                if (text[i] == '\\') escaped = true;
                else if (text[i] == target) return i;
            }
            return -1;
        }

        // An attribute type is a descriptor: a letter followed by letters, digits, or hyphens.
        // Numeric OID forms are not published by this directory, so they are not accepted either —
        // an unrecognized type makes the DN unparseable rather than silently matching nothing.
        private static bool IsAttributeType(string value)
        {
            if (value.Length == 0 || !char.IsAsciiLetter(value[0]))
                return false;
            foreach (var c in value)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c != '-')
                    return false;
            }
            return true;
        }

        private static bool IsHex(char c) => char.IsAsciiHexDigit(c);
    }
}
