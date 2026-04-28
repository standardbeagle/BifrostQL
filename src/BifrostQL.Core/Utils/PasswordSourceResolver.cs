namespace BifrostQL.Core.Utils
{
    /// <summary>
    /// Pure, stateless password-source resolution for CLI commands that accept
    /// a credential via <c>--password</c>, <c>--password-stdin</c>, or interactive
    /// prompt. The logic is extracted from <c>VaultCommands</c> so the precedence
    /// rules are unit-testable without spawning a process or stubbing
    /// <see cref="System.Console"/>.
    ///
    /// Precedence (highest first):
    /// <list type="number">
    ///   <item><description>Both <c>--password</c> and <c>--password-stdin</c> → error (mutually exclusive).</description></item>
    ///   <item><description><c>--password-stdin</c> + stdin is a tty → error (refuses to prompt).</description></item>
    ///   <item><description><c>--password-stdin</c> + redirected stdin → read one line; error on empty.</description></item>
    ///   <item><description><c>--password &lt;value&gt;</c> → use the value verbatim.</description></item>
    ///   <item><description>Neither flag + tty stdin → interactive (<see cref="PasswordSourceKind.Interactive"/>).</description></item>
    ///   <item><description>Neither flag + redirected stdin → legacy back-compat: read stdin.
    ///     Callers should emit a deprecation warning recommending <c>--password-stdin</c>.</description></item>
    /// </list>
    ///
    /// The resolver is a pure function: it takes a <see cref="System.Func{TResult}"/>
    /// for stdin reads so tests can inject deterministic input without touching
    /// <see cref="System.Console.In"/>.
    /// </summary>
    public static class PasswordSourceResolver
    {
        /// <summary>
        /// Resolves the effective password source according to the precedence
        /// table documented on <see cref="PasswordSourceResolver"/>.
        /// </summary>
        /// <param name="hasPasswordFlag">True if <c>--password</c> was provided on the command line (even with empty value).</param>
        /// <param name="passwordValue">Value of the <c>--password</c> flag (may be null/empty).</param>
        /// <param name="passwordStdin">True if <c>--password-stdin</c> was provided.</param>
        /// <param name="stdinIsRedirected">True when <c>Console.IsInputRedirected</c> is true.</param>
        /// <param name="readStdinLine">Factory that returns the next line from stdin. Invoked at most once.</param>
        /// <returns>
        /// A <see cref="PasswordSourceResult"/> describing the resolved value, an
        /// error message, or a request for interactive prompting.
        /// </returns>
        public static PasswordSourceResult Resolve(
            bool hasPasswordFlag,
            string? passwordValue,
            bool passwordStdin,
            bool stdinIsRedirected,
            System.Func<string?> readStdinLine)
        {
            if (readStdinLine is null)
                throw new System.ArgumentNullException(nameof(readStdinLine));

            // 1. Mutually exclusive flags.
            if (hasPasswordFlag && passwordStdin)
                return PasswordSourceResult.Error("Error: --password and --password-stdin are mutually exclusive");

            // 2. --password-stdin path.
            if (passwordStdin)
            {
                if (!stdinIsRedirected)
                    return PasswordSourceResult.Error("Error: --password-stdin requires stdin to be redirected");

                var line = readStdinLine() ?? string.Empty;
                // Trim trailing whitespace only — a leading space is a legal password character.
                line = line.TrimEnd('\r', '\n', ' ', '\t');
                if (line.Length == 0)
                    return PasswordSourceResult.Error("Error: --password-stdin received empty input");

                return PasswordSourceResult.FromValue(line, legacyStdinFallback: false);
            }

            // 3. Explicit --password value wins over prompting.
            if (hasPasswordFlag)
                return PasswordSourceResult.FromValue(passwordValue ?? string.Empty, legacyStdinFallback: false);

            // 4. Neither flag: defer to interactive or legacy-stdin path.
            if (!stdinIsRedirected)
                return PasswordSourceResult.InteractivePrompt();

            // 5. Legacy back-compat: redirected stdin with no explicit flag. We still
            // read it so existing scripts keep working, but the caller should emit a
            // stderr warning recommending --password-stdin.
            var legacyLine = readStdinLine() ?? string.Empty;
            legacyLine = legacyLine.TrimEnd('\r', '\n', ' ', '\t');
            return PasswordSourceResult.FromValue(legacyLine, legacyStdinFallback: true);
        }
    }

    /// <summary>
    /// Outcome of <see cref="PasswordSourceResolver.Resolve"/>. Exactly one of
    /// <see cref="Value"/>, <see cref="ErrorMessage"/>, or
    /// <see cref="Kind"/> == <see cref="PasswordSourceKind.Interactive"/> is meaningful.
    /// </summary>
    public readonly record struct PasswordSourceResult(
        PasswordSourceKind Kind,
        string? Value,
        string? ErrorMessage,
        bool LegacyStdinFallback)
    {
        /// <summary>Resolved to a concrete password string.</summary>
        public static PasswordSourceResult FromValue(string value, bool legacyStdinFallback)
            => new(PasswordSourceKind.Value, value, null, legacyStdinFallback);

        /// <summary>Resolved to an interactive prompt request.</summary>
        public static PasswordSourceResult InteractivePrompt()
            => new(PasswordSourceKind.Interactive, null, null, false);

        /// <summary>Resolved to a user-facing error.</summary>
        public static PasswordSourceResult Error(string message)
            => new(PasswordSourceKind.Error, null, message, false);
    }

    /// <summary>Discriminator for <see cref="PasswordSourceResult"/>.</summary>
    public enum PasswordSourceKind
    {
        /// <summary>A concrete password value was produced.</summary>
        Value,
        /// <summary>The caller should prompt the user interactively (tty).</summary>
        Interactive,
        /// <summary>Input validation failed; see <see cref="PasswordSourceResult.ErrorMessage"/>.</summary>
        Error,
    }
}
