using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BifrostQL.Server.Ldap
{
    /// <summary>
    /// The LDAPv3 front door as an <see cref="IProtocolAdapter"/>. Per-connection work is done by
    /// <see cref="LdapConnectionHandler"/> bound onto a Kestrel TCP listener (see
    /// <c>AddBifrostLdap</c>); this adapter owns the front-door lifecycle and the startup-time
    /// configuration guard.
    ///
    /// <para><b>Fail fast.</b> Startup validates the options and throws on any invalid limit —
    /// a thrown <see cref="StartAsync"/> aborts host startup (the <see cref="IProtocolAdapter"/>
    /// contract), so a misconfigured front door never comes up half-listening. A socket bind failure
    /// (port in use) is surfaced by Kestrel's own listener at host start for the same fail-fast
    /// result. Note this slice does not authenticate binds or execute searches; it is a codec +
    /// connection-lifecycle surface only.</para>
    /// </summary>
    public sealed class LdapWireAdapter : IProtocolAdapter
    {
        private readonly LdapWireOptions _options;
        private readonly ILogger<LdapWireAdapter> _logger;

        public LdapWireAdapter(LdapWireOptions options, ILogger<LdapWireAdapter>? logger = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? NullLogger<LdapWireAdapter>.Instance;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Validate(_options);

            _logger.LogInformation(
                "ldap front door ready on port {Port} (codec + connection lifecycle only; bind auth, search, " +
                "TLS, SASL, and writes are not enabled in this slice). Limits: max message {MaxMessage} bytes, " +
                "nesting depth {Depth}, connections {MaxConnections}, idle {Idle}.",
                _options.Port, _options.MaxMessageLength, _options.MaxNestingDepth,
                _options.MaxConnections, _options.IdleTimeout);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <summary>
        /// Fails fast on any invalid limit so the front door never starts in a state that would
        /// disable a DoS guard or bind an illegal port. Called at startup (aborting host startup on
        /// failure) and reused by the registration so a bad config is caught before the listener binds.
        /// </summary>
        internal static void Validate(LdapWireOptions options)
        {
            if (options.Port is < 1 or > 65535)
                throw new ArgumentOutOfRangeException(nameof(options.Port), options.Port, "ldap Port must be 1..65535.");
            if (options.MaxMessageLength < 16)
                throw new ArgumentOutOfRangeException(nameof(options.MaxMessageLength), options.MaxMessageLength,
                    "ldap MaxMessageLength must be at least 16 bytes.");
            if (options.MaxNestingDepth < 1)
                throw new ArgumentOutOfRangeException(nameof(options.MaxNestingDepth), options.MaxNestingDepth,
                    "ldap MaxNestingDepth must be at least 1.");
            if (options.MaxFilterComponents < 1)
                throw new ArgumentOutOfRangeException(nameof(options.MaxFilterComponents), options.MaxFilterComponents,
                    "ldap MaxFilterComponents must be at least 1.");
            if (options.MaxSearchAttributes < 1)
                throw new ArgumentOutOfRangeException(nameof(options.MaxSearchAttributes), options.MaxSearchAttributes,
                    "ldap MaxSearchAttributes must be at least 1.");
            if (options.MaxConnections < 1)
                throw new ArgumentOutOfRangeException(nameof(options.MaxConnections), options.MaxConnections,
                    "ldap MaxConnections must be at least 1.");
            if (options.MaxOutstandingOperations < 1)
                throw new ArgumentOutOfRangeException(nameof(options.MaxOutstandingOperations), options.MaxOutstandingOperations,
                    "ldap MaxOutstandingOperations must be at least 1.");
            if (options.IdleTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(options.IdleTimeout), options.IdleTimeout,
                    "ldap IdleTimeout must be positive.");
            if (options.MaxPasswordLength < 1)
                throw new ArgumentOutOfRangeException(nameof(options.MaxPasswordLength), options.MaxPasswordLength,
                    "ldap MaxPasswordLength must be at least 1.");
            if (options.MaxBindAttemptsPerSource < 1)
                throw new ArgumentOutOfRangeException(nameof(options.MaxBindAttemptsPerSource), options.MaxBindAttemptsPerSource,
                    "ldap MaxBindAttemptsPerSource must be at least 1.");
            if (options.MaxBindAttemptsPerAccount < 1)
                throw new ArgumentOutOfRangeException(nameof(options.MaxBindAttemptsPerAccount), options.MaxBindAttemptsPerAccount,
                    "ldap MaxBindAttemptsPerAccount must be at least 1.");
            if (options.BindRateLimitWindow <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(options.BindRateLimitWindow), options.BindRateLimitWindow,
                    "ldap BindRateLimitWindow must be positive.");
            if (options.AuthenticationTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(options.AuthenticationTimeout), options.AuthenticationTimeout,
                    "ldap AuthenticationTimeout must be positive.");
            if (options.BindAddress is null)
                throw new ArgumentNullException(nameof(options.BindAddress),
                    "ldap BindAddress must be set; it defaults to loopback and widening it is explicit.");
        }
    }
}
