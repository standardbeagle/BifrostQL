namespace BifrostQL.Server.Resp
{
    /// <summary>
    /// Per-connection RESP session state: the negotiated protocol version, the
    /// authentication outcome and the Bifrost user context an authenticated login projected
    /// to. Mutated only by the connection loop (AUTH / HELLO / RESET); read by command
    /// handlers. The <see cref="UserContext"/> is the SAME identity projection every other
    /// transport gate uses — data commands in later slices execute reads under it through
    /// <c>IQueryIntentExecutor</c>, so the security transformer pipeline is unskippable.
    /// </summary>
    internal sealed class RespSession
    {
        public RespSession(long id) => Id = id;

        /// <summary>Monotonic per-front-door connection id, surfaced in HELLO/INFO.</summary>
        public long Id { get; }

        /// <summary>Negotiated RESP protocol version (2 until a client sends <c>HELLO 3</c>).</summary>
        public int Protocol { get; set; } = RespProtocol.Resp2;

        /// <summary>True once a login authenticated AND projected to a real Bifrost identity.</summary>
        public bool IsAuthenticated { get; private set; }

        /// <summary>The projected Bifrost user context; empty until authenticated.</summary>
        public IDictionary<string, object?> UserContext { get; private set; } = new Dictionary<string, object?>();

        /// <summary>The optional client-supplied connection name (HELLO SETNAME / CLIENT SETNAME).</summary>
        public string? Name { get; set; }

        /// <summary>Marks the session authenticated under the given projected identity. Never anonymous.</summary>
        public void Authenticate(IDictionary<string, object?> userContext)
        {
            UserContext = userContext;
            IsAuthenticated = true;
        }

        /// <summary>Returns the session to its just-connected state (RESET): deauthenticated, RESP2, no name.</summary>
        public void Reset()
        {
            IsAuthenticated = false;
            UserContext = new Dictionary<string, object?>();
            Protocol = RespProtocol.Resp2;
            Name = null;
        }
    }

    /// <summary>
    /// The context a data command handler receives: the parsed command arguments (element 0
    /// is the command name), the authenticated <see cref="RespSession"/>, the resolving
    /// service provider (for <c>IQueryIntentExecutor</c> / translator seams), and the
    /// configured endpoint. Slice-1 plumbing commands are handled inline in the connection
    /// loop because they mutate session state; this context is what the SLICE 2-5 data
    /// commands attach against.
    /// </summary>
    internal sealed record RespCommandContext(
        IReadOnlyList<string> Arguments,
        RespSession Session,
        IServiceProvider Services,
        string? Endpoint);

    /// <summary>
    /// The dispatch seam for a RESP data command (GET/MGET/HGETALL/SCAN/SET…). Slice 1 lands
    /// the interface and the case-insensitive dispatch table; the concrete read/write command
    /// handlers attach in later slices via DI registration, with zero edits to the codec or
    /// the connection loop — mirroring the pgwire <c>IPgQueryTranslator</c> seam discipline
    /// (swap one registration line). Reads MUST route through <c>IQueryIntentExecutor</c> and
    /// writes through <c>IMutationIntentExecutor</c> under <see cref="RespSession.UserContext"/>
    /// so the transformer pipeline is unskippable — a handler must never touch a database
    /// directly.
    /// </summary>
    internal interface IRespCommandHandler
    {
        /// <summary>The command keyword this handler answers, upper-case (e.g. <c>GET</c>).</summary>
        string Name { get; }

        /// <summary>
        /// Whether this command needs an established identity. When true and the session is
        /// unauthenticated on an auth-required front door, the loop answers <c>NOAUTH</c>
        /// before the handler runs. Data commands are true.
        /// </summary>
        bool RequiresAuthentication { get; }

        /// <summary>Produces the RESP reply for this command.</summary>
        Task<RespValue> HandleAsync(RespCommandContext context, CancellationToken cancellationToken);
    }
}
