using BifrostQL.Core.Modules.OperationId;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Server
{
    /// <summary>
    /// The client-safe wording a non-GraphQL wire carries for an operation-id refusal, shared by
    /// every adapter error funnel (gRPC, RESP, MCP) so one condition reads the same on each.
    ///
    /// <para>The refusal is a condition the client must act on (send an id, use a new id), not a
    /// server fault, so reporting it as an internal error tells the client to retry a write that
    /// can never succeed. Each wire leads with the stable <see cref="BifrostExecutionError.ErrorCode"/>
    /// the GraphQL wire carries in <c>extensions.code</c>. The description is a constant per code
    /// and names no table, column or key: invariant 3 forbids forwarding
    /// <see cref="BifrostExecutionError.Message"/> itself.</para>
    /// </summary>
    internal static class OperationIdWireRefusal
    {
        /// <summary>
        /// The code and description for <paramref name="error"/> when it is an operation-id
        /// refusal, or null for any other error.
        /// </summary>
        public static (string Code, string Description)? Of(Exception error)
        {
            if (error is not BifrostExecutionError { ErrorCode: { } code })
                return null;
            string? description = code switch
            {
                OperationIdExecution.RequiredCode => "this write requires an operation id.",
                OperationIdExecution.UnsupportedCode => "this write cannot record an operation id.",
                OperationIdExecution.ReusedCode => "this operation id was already used for a different write.",
                OperationIdExecution.AnonymousCode => "an operation id needs a caller with a user id.",
                _ => null,
            };
            return description is null ? null : (code, description);
        }

        /// <summary>
        /// <c>CODE: description</c> for <paramref name="error"/> when it is an operation-id
        /// refusal, or null for any other error. RESP formats <see cref="Of"/> itself, because a
        /// Redis error's first word is its kind.
        /// </summary>
        public static string? TextOf(Exception error)
            => Of(error) is var (code, description) ? code + ": " + description : null;

        /// <summary>
        /// Whether <paramref name="error"/> refuses an id already recorded for another write: a
        /// conflict with stored state, where the other refusals are a request the server cannot
        /// accept as sent.
        /// </summary>
        public static bool IsReused(Exception error)
            => error is BifrostExecutionError { ErrorCode: OperationIdExecution.ReusedCode };
    }
}
