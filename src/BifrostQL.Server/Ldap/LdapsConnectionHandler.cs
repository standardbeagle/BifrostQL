using System.Net.Security;
using System.Security.Authentication;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using BifrostQL.Server.Pgwire; // DuplexPipeStream: shared Kestrel IDuplexPipe→Stream glue, not pgwire-specific.

namespace BifrostQL.Server.Ldap
{
    /// <summary>
    /// Kestrel connection handler for the IMPLICIT-TLS (LDAPS) listener. It owns exactly one thing
    /// the cleartext handler does not: the handshake that happens before any LDAP byte exists. Once
    /// the connection is confidential it hands off to the SAME
    /// <see cref="LdapConnectionHandler"/> session loop, marked as already-confidential — so LDAPS
    /// and StartTLS converge on one implementation of every bind, search and lifecycle rule rather
    /// than two that can drift.
    ///
    /// <para><b>Admission at ACCEPT.</b> The connection slot is taken before the handshake, not
    /// after: a cap applied after the handshake would bound only established sessions while an
    /// unauthenticated peer could still force unbounded handshake work. The handshake then runs under
    /// its own deadline, so holding a slot open mid-handshake is bounded too. Both listeners share
    /// one counter, because <see cref="LdapWireOptions.MaxConnections"/> bounds this front door's
    /// total concurrent connections — opening a second port must not double the ceiling.</para>
    ///
    /// <para>A failed handshake closes silently. Nothing can be said on this wire in the clear: the
    /// peer is speaking TLS and an LDAP message written here would be unreadable noise at best.</para>
    /// </summary>
    internal sealed class LdapsConnectionHandler : ConnectionHandler
    {
        private readonly LdapWireOptions _options;
        private readonly LdapBoundedCounter _connections;
        private readonly LdapTlsProvider _tls;
        private readonly LdapConnectionHandler _session;
        private readonly ILogger<LdapsConnectionHandler> _logger;

        public LdapsConnectionHandler(
            LdapWireOptions options,
            LdapBoundedCounter connections,
            LdapTlsProvider tls,
            LdapConnectionHandler session,
            ILogger<LdapsConnectionHandler>? logger = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _connections = connections ?? throw new ArgumentNullException(nameof(connections));
            _tls = tls ?? throw new ArgumentNullException(nameof(tls));
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _logger = logger ?? NullLogger<LdapsConnectionHandler>.Instance;
        }

        public override async Task OnConnectedAsync(ConnectionContext connection)
        {
            if (!_connections.TryAcquire())
            {
                _logger.LogWarning("ldaps connection refused: at the {Max}-connection cap.", _options.MaxConnections);
                return;
            }
            try
            {
                await using var transport = new DuplexPipeStream(connection.Transport);
                var source = connection.RemoteEndPoint?.ToString() ?? "unknown";

                SslStream ssl;
                try
                {
                    ssl = await _tls.AuthenticateAsync(transport, _options.TlsHandshakeTimeout, connection.ConnectionClosed);
                }
                catch (Exception ex) when (ex is AuthenticationException
                                              or IOException
                                              or OperationCanceledException
                                              or InvalidOperationException
                                              or ObjectDisposedException)
                {
                    _logger.LogDebug(ex, "ldaps handshake failed; closing the connection.");
                    return;
                }

                await using (ssl)
                    await _session.RunSessionAsync(ssl, connection.ConnectionClosed, source, tlsEstablished: true);
            }
            finally
            {
                _connections.Release();
            }
        }
    }
}
