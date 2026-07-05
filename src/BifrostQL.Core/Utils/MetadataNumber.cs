using System.Globalization;

namespace BifrostQL.Core.Utils
{
    /// <summary>
    /// Parses numeric metadata/config values with fail-fast semantics: an absent
    /// value falls back to the supplied default (the default was explicitly asked
    /// for), but a value that is present yet unparseable or out of range is a
    /// configuration error and throws rather than silently reverting to the
    /// default. A silent revert hides operator typos (e.g. <c>max-rows: 1O0</c>)
    /// and lets a mistyped limit quietly take effect.
    /// </summary>
    public static class MetadataNumber
    {
        /// <summary>
        /// Resolves a positive <see cref="int"/> metadata value. Null/blank yields
        /// <paramref name="defaultValue"/>; a present value that is not a positive
        /// integer throws <see cref="InvalidOperationException"/> naming the key.
        /// </summary>
        public static int PositiveInt(string? value, int defaultValue, string key)
        {
            if (string.IsNullOrWhiteSpace(value))
                return defaultValue;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
                return parsed;
            throw new InvalidOperationException(
                $"Metadata '{key}' must be a positive integer, but was '{value}'.");
        }

        /// <summary>
        /// Resolves an optional positive <see cref="int"/> metadata value. Null/blank
        /// yields <c>null</c> (the constraint is simply absent); a present value that
        /// is not a positive integer throws <see cref="InvalidOperationException"/>.
        /// </summary>
        public static int? PositiveIntOrNull(string? value, string key)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
                return parsed;
            throw new InvalidOperationException(
                $"Metadata '{key}' must be a positive integer, but was '{value}'.");
        }

        /// <summary>
        /// Resolves a positive <see cref="long"/> metadata value. Null/blank yields
        /// <paramref name="defaultValue"/>; a present value that is not a positive
        /// long throws <see cref="InvalidOperationException"/> naming the key.
        /// </summary>
        public static long PositiveLong(string? value, long defaultValue, string key)
        {
            if (string.IsNullOrWhiteSpace(value))
                return defaultValue;
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
                return parsed;
            throw new InvalidOperationException(
                $"Metadata '{key}' must be a positive integer, but was '{value}'.");
        }
    }
}
