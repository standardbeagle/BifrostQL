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

        /// <summary>
        /// Strict variant for explicit single-value boolean config keys (not the
        /// breadth-matched list values <see cref="Parse"/> also serves). Null/blank
        /// yields <paramref name="defaultValue"/>, a recognized on/off token yields
        /// its value, and any other present token throws
        /// <see cref="InvalidOperationException"/> naming the key — so an operator
        /// typo (e.g. <c>path-style: yess</c>) fails rather than silently reverting
        /// to the default.
        /// </summary>
        public static bool ParseStrict(string? value, bool defaultValue, string key)
        {
            var v = value?.Trim();
            if (string.IsNullOrEmpty(v))
                return defaultValue;
            if (OnTokens.Contains(v))
                return true;
            if (OffTokens.Contains(v))
                return false;
            throw new InvalidOperationException(
                $"Metadata '{key}' must be a boolean switch (true/false/on/off/yes/no/1/0), but was '{value}'.");
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
