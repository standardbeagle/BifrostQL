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
    ///
    /// <para>An authorization denial is mapped by CONDITION, not lumped into the internal
    /// fault bucket. The policy transformers tag their throw with
    /// <see cref="BifrostQL.Core.Resolvers.BifrostExecutionError.AccessDeniedCode"/>, and a
    /// funnel that ignores that tag reports a denial as XX000 — the SQLSTATE that tells a
    /// driver the server faulted and the request is worth retrying. A denial can never
    /// succeed on retry, so it maps to 42501 (insufficient_privilege), which clients
    /// already treat as terminal. Only the code and the category cross the wire; the
    /// message stays identifier-free, so this does not weaken invariant 3. See invariant
    /// 10 — map by condition so the same condition reads the same on every door.</para>
    /// </summary>
    internal static class PgQueryError
    {
        public static (string SqlState, string Message) Map(Exception ex) => ex switch
        {
            PgQueryTranslationException t => (t.SqlState, t.Message),
            BifrostQL.Core.Resolvers.BifrostExecutionError b
                when b.ErrorCode == BifrostQL.Core.Resolvers.BifrostExecutionError.AccessDeniedCode
                => (PgWireProtocol.SqlStateInsufficientPrivilege, PgWireProtocol.AccessDeniedErrorMessage),
            _ => (PgWireProtocol.SqlStateInternalError, PgWireProtocol.InternalQueryErrorMessage),
        };
    }
}
