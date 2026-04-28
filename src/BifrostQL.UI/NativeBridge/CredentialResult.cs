namespace BifrostQL.UI.NativeBridge
{
    /// <summary>
    /// Result envelope returned by
    /// <see cref="CredentialPromptWindow.PromptAsync"/>.
    ///
    /// Three shapes in one record, deliberately chosen so the caller can
    /// pattern-match on <see cref="IsSaved"/> and <see cref="ErrorMessage"/>
    /// without having to carry a discriminated union through the codebase.
    ///
    /// <para>
    /// <b>Security.</b> The <see cref="Password"/> field is sensitive and
    /// must never appear in logs. The default <c>record</c>-generated
    /// <c>ToString()</c> would print every property verbatim, so we override
    /// it to emit a redacted string. Callers that need the password should
    /// read <see cref="Password"/> directly and null the local variable out
    /// as soon as the credential has been handed to the vault writer.
    /// </para>
    ///
    /// <para>
    /// <b>Invariants.</b> When <see cref="IsSaved"/> is <see langword="true"/>,
    /// <see cref="Username"/> is non-null. When <see cref="IsSaved"/> is
    /// <see langword="false"/>, both <see cref="Username"/> and
    /// <see cref="Password"/> are <see langword="null"/>; an optional
    /// <see cref="ErrorMessage"/> may describe the failure.
    /// </para>
    /// </summary>
    public sealed record CredentialResult
    {
        /// <summary>Whether the user clicked Save with non-empty credentials.</summary>
        public bool IsSaved { get; init; }

        /// <summary>The username entered. Non-null when <see cref="IsSaved"/> is true.</summary>
        public string? Username { get; init; }

        /// <summary>
        /// The password entered. Sensitive — never log this value, and null
        /// the local variable after handing it to the vault writer.
        /// </summary>
        public string? Password { get; init; }

        /// <summary>Optional error message for the Failed shape.</summary>
        public string? ErrorMessage { get; init; }

        /// <summary>Builds a Saved result. Neither argument is null-checked so
        /// the factory stays cheap; callers pass already-validated strings.</summary>
        public static CredentialResult Saved(string username, string password) =>
            new() { IsSaved = true, Username = username, Password = password };

        /// <summary>Builds a Cancelled result (user hit Cancel / Escape / closed window).</summary>
        public static CredentialResult Cancelled() => new() { IsSaved = false };

        /// <summary>Builds a Failed result with a human-readable error.</summary>
        public static CredentialResult Failed(string error) =>
            new() { IsSaved = false, ErrorMessage = error };

        /// <summary>
        /// Redacted string form. The default record <c>ToString()</c>
        /// prints every property, which would leak the password into any
        /// log line the caller writes. This override replaces the password
        /// with a literal <c>&lt;redacted&gt;</c> token and keeps the
        /// Username + ErrorMessage for debuggability.
        /// </summary>
        public override string ToString() => IsSaved
            ? $"CredentialResult {{ IsSaved = true, Username = {Username}, Password = <redacted> }}"
            : $"CredentialResult {{ IsSaved = false, ErrorMessage = {ErrorMessage} }}";
    }
}
