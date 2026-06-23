namespace BifrostQL.Core.Utils
{
    /// <summary>
    /// Uniform activate/deactivate vocabulary for metadata switches. Auto-detection
    /// casts a wide net ("multi-breadth matching"); these helpers let metadata both
    /// turn a switch on and explicitly turn it off using one consistent token set,
    /// plus an inline <c>!</c> negation prefix for pruning a single match out of a
    /// comma-separated (breadth-matched) list value.
    /// </summary>
    public static class MetadataSwitch
    {
        private static readonly HashSet<string> OnTokens = new(StringComparer.OrdinalIgnoreCase)
        {
            "true", "on", "yes", "enabled", "enable", "1", "active", "activate",
        };

        private static readonly HashSet<string> OffTokens = new(StringComparer.OrdinalIgnoreCase)
        {
            "false", "off", "no", "disabled", "disable", "0", "inactive", "deactivate", "none", "!",
        };

        /// <summary>
        /// Resolves a boolean switch value. Recognizes the on/off vocabulary and a
        /// leading <c>!</c> negation. Returns <paramref name="defaultValue"/> when
        /// the value is null/blank or unrecognized, so callers keep their existing
        /// default-on or default-off behavior.
        /// </summary>
        public static bool Parse(string? value, bool defaultValue)
        {
            var v = value?.Trim();
            if (string.IsNullOrEmpty(v))
                return defaultValue;
            if (v.StartsWith("!", StringComparison.Ordinal))
                return false;
            if (OnTokens.Contains(v))
                return true;
            if (OffTokens.Contains(v))
                return false;
            return defaultValue;
        }

        /// <summary>True when a list entry is negated with a leading <c>!</c>.</summary>
        public static bool IsNegated(string? entry)
            => entry != null && entry.TrimStart().StartsWith("!", StringComparison.Ordinal);

        /// <summary>
        /// Strips a leading <c>!</c> (and surrounding whitespace) from a list entry,
        /// returning the bare value (e.g. <c>"!Roles:UserRoles"</c> -&gt;
        /// <c>"Roles:UserRoles"</c>).
        /// </summary>
        public static string StripNegation(string entry)
        {
            var e = entry.Trim();
            return e.StartsWith("!", StringComparison.Ordinal) ? e[1..].Trim() : e;
        }
    }
}
