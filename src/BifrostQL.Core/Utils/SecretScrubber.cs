using System.Text.RegularExpressions;

namespace BifrostQL.Core.Utils
{
    /// <summary>
    /// Stateless, thread-safe utility for stripping credential-like substrings from
    /// arbitrary text before it leaves the process (error responses, log messages,
    /// diagnostic dumps, etc.).
    ///
    /// The scrubber is intentionally conservative: it masks any value that *looks*
    /// like a secret, even if the surrounding text is technically harmless. The
    /// cost of a false positive (an unreadable error field) is far lower than the
    /// cost of a false negative (a leaked password in the browser).
    ///
    /// Patterns handled (case-insensitive):
    /// <list type="bullet">
    ///   <item><description><c>Password=...</c> and <c>Pwd=...</c> up to <c>;</c>, <c>"</c>, or end-of-string</description></item>
    ///   <item><description><c>User Id=...</c> and <c>Uid=...</c> (PII) with the same termination rules</description></item>
    ///   <item><description>URL-style credentials <c>scheme://user:password@host</c> (only the password portion)</description></item>
    ///   <item><description><c>BIFROST_SERVERS=...</c> env-var dumps (entire value to end-of-line/string)</description></item>
    /// </list>
    ///
    /// Null input passes through as null; empty input passes through as empty.
    /// </summary>
    public static class SecretScrubber
    {
        private const string Mask = "****";

        // Compiled regexes are cached statically. RegexOptions.Compiled + IgnoreCase
        // gives us sub-microsecond matching for the short strings the scrubber is
        // typically invoked on.
        private static readonly RegexOptions Opts =
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        // "Password=" / "Pwd=" followed by:
        //   1. a quoted value "..." (which may itself contain ; characters), OR
        //   2. a plain value up to the next ; or end-of-string.
        // The key itself is captured so we can preserve its original casing.
        // Quoted form is tried first via the alternation ordering so that the
        // "value";x=1 case consumes the whole quoted segment.
        private static readonly Regex PasswordRegex = new(
            @"(?<key>Password|Pwd)\s*=\s*(?:""[^""]*""|[^;""]*)",
            Opts);

        // "User Id=" / "Uid=" — same dual-form handling as Password above.
        // "User Id" allows one or more whitespace characters between the two words
        // to match the Microsoft ADO.NET connection-string conventions.
        private static readonly Regex UserIdRegex = new(
            @"(?<key>User\s+Id|Uid)\s*=\s*(?:""[^""]*""|[^;""]*)",
            Opts);

        // URL credentials: scheme://user:password@host
        // The scheme is restricted to a small character class (RFC 3986 minus dots)
        // to avoid accidentally matching non-URL "foo://" prefixes. The user token
        // is preserved; only the password token is replaced.
        private static readonly Regex UrlCredsRegex = new(
            @"(?<scheme>[A-Za-z][A-Za-z0-9+.\-]*://)(?<user>[^:/@\s]+):(?<pw>[^@\s]+)@",
            Opts);

        // BIFROST_SERVERS env-var dumps: mask the entire value up to end-of-line
        // or end-of-string. Anything inside is assumed to be JSON with secrets.
        private static readonly Regex BifrostServersRegex = new(
            @"BIFROST_SERVERS\s*=\s*[^\r\n]*",
            Opts);

        /// <summary>
        /// Returns a copy of <paramref name="input"/> with credential-like
        /// substrings replaced by <c>****</c>. Null input returns null;
        /// empty input returns empty.
        /// </summary>
        /// <param name="input">The string to scrub. May be null.</param>
        /// <returns>The scrubbed string, or null if <paramref name="input"/> was null.</returns>
        public static string? Scrub(string? input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            var result = input;

            // Order matters: the URL pattern runs first so its password portion
            // is masked before the connection-string patterns get a chance to
            // match any lookalike fragments inside a URL.
            result = UrlCredsRegex.Replace(result, m =>
                $"{m.Groups["scheme"].Value}{m.Groups["user"].Value}:{Mask}@");

            result = BifrostServersRegex.Replace(result, $"BIFROST_SERVERS={Mask}");

            result = PasswordRegex.Replace(result, m => $"{m.Groups["key"].Value}={Mask}");

            result = UserIdRegex.Replace(result, m => $"{m.Groups["key"].Value}={Mask}");

            return result;
        }
    }
}
