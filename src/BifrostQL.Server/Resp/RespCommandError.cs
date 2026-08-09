using BifrostQL.Core.Resolvers;

namespace BifrostQL.Server.Resp
{
    /// <summary>
    /// The single funnel mapping a data-command exception to the client-safe RESP error
    /// the wire may carry. Every command class — read, scan, write — maps through here so
    /// the same condition cannot read differently depending on which command hit it.
    ///
    /// <para>Bifrost-internal exception text is untrusted on a client-facing wire (it can
    /// carry schema/driver detail), so nothing is forwarded verbatim: the real exception is
    /// logged server-side and only a fixed, identifier-free string crosses the wire. See
    /// <c>.claude/rules/protocol-adapter-security.md</c> invariant 3.</para>
    ///
    /// <para>An authorization denial is mapped by CONDITION rather than lumped into the
    /// internal-fault bucket. The policy transformers tag their throw with
    /// <see cref="BifrostExecutionError.AccessDeniedCode"/>; reporting that as
    /// <c>-ERR internal error</c> tells the client the server faulted and the command is
    /// worth retrying, when in fact it can never succeed. <c>-NOPERM</c> is the prefix
    /// Redis clients already treat as a terminal permission failure. The category crosses
    /// the wire; the denied table or column never does. See invariant 10.</para>
    /// </summary>
    internal static class RespCommandError
    {
        public static string Map(Exception ex) =>
            ex is BifrostExecutionError b && b.ErrorCode == BifrostExecutionError.AccessDeniedCode
                ? RespProtocol.AccessDeniedError
                : RespProtocol.InternalError;
    }
}
