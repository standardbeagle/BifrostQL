namespace BifrostQL.Mcp
{
    /// <summary>
    /// A request whose caller identity could not be (re-)established: no HTTP context, no
    /// credential where the session was opened with one, a credential that no longer
    /// matches the session's, or one that no longer resolves to a principal.
    ///
    /// <para>Its message is a CONSTANT: it names no issuer, token, session or user, so it
    /// carries no probe channel and nothing needs sanitizing downstream (invariant 3). It
    /// is a member of <c>BifrostMcpServerFactory</c>'s funnelled condition set, so like
    /// every other condition on this front door it reaches the wire as a mapped result and
    /// never escapes to the host (invariant 1).</para>
    /// </summary>
    internal sealed class McpIdentityException : Exception
    {
        internal McpIdentityException()
            : base("Authentication failed: this request did not present a valid identity for the session.")
        {
        }
    }
}
