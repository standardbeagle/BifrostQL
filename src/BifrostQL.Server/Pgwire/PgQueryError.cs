namespace BifrostQL.Server.Pgwire
{
    /// <summary>
    /// Maps a query-phase exception to the client-safe <c>(SQLSTATE, message)</c> pair the
    /// wire may carry. Shared by the simple-query loop and the extended query protocol so
    /// both sanitize identically.
    ///
    /// <para>Only a <see cref="PgQueryTranslationException"/> — the recognizer's own curated,
    /// deliberately user-facing message (bad syntax, unknown relation/column) — is forwarded
    /// verbatim, with its own SQLSTATE. Every other exception, including
    /// <see cref="BifrostQL.Core.Resolvers.BifrostExecutionError"/>, is sanitized to a generic
    /// internal_error: its message is NOT provably leak-free (it can wrap raw driver/DB text
    /// or caller-supplied detail), so forwarding it could leak schema/infrastructure detail.
    /// The full exception is logged server-side by the caller; only the sanitized string
    /// crosses the wire. Fail closed toward sanitization — see
    /// <c>.claude/rules/protocol-adapter-security.md</c> invariant 3.</para>
    /// </summary>
    internal static class PgQueryError
    {
        public static (string SqlState, string Message) Map(Exception ex) => ex switch
        {
            PgQueryTranslationException t => (t.SqlState, t.Message),
            _ => (PgWireProtocol.SqlStateInternalError, PgWireProtocol.InternalQueryErrorMessage),
        };
    }
}
