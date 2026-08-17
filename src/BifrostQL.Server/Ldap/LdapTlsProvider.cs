using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace BifrostQL.Server.Ldap
{
    /// <summary>
    /// Owns the LDAP front door's server certificate and performs the server half of a TLS
    /// handshake. ONE provider serves both confidential surfaces — the LDAPS listener and the
    /// StartTLS upgrade on the cleartext listener — so the two can never end up on different
    /// certificates or different protocol floors.
    ///
    /// <para><b>Fail fast, never fall back.</b> The certificate is resolved once at registration. A
    /// missing file, a wrong password, a corrupt file, or a certificate without its private key
    /// aborts startup with <see cref="LdapConfigurationException"/>. There is no runtime fallback:
    /// falling back would silently turn a confidential front door into a cleartext one, which is the
    /// exact downgrade the transport gate exists to prevent (mirrors the gRPC adapter's TLS
    /// configuration, which was fixed after shipping precisely that fallback).</para>
    ///
    /// <para><b>Policy.</b> TLS 1.2 / 1.3 only, and no client certificate is requested — matching
    /// the pgwire listener's SSLRequest upgrade, the repo's existing SslStream front door. LDAP
    /// authenticates through bind, so a client certificate would be collected and never consulted;
    /// SASL/EXTERNAL certificate identity is a non-goal here.</para>
    /// </summary>
    internal sealed class LdapTlsProvider
    {
        private readonly X509Certificate2 _certificate;

        private LdapTlsProvider(X509Certificate2 certificate) => _certificate = certificate;

        /// <summary>
        /// Resolves the configured certificate, or returns <c>null</c> when the deployment configured
        /// none (a cleartext-only front door: StartTLS is then answered <c>unavailable</c> and every
        /// credentialed bind is refused). Throws <see cref="LdapConfigurationException"/> when a
        /// certificate WAS configured but cannot be used — a configured-but-broken certificate is a
        /// startup failure, never a silent downgrade to "no TLS".
        /// </summary>
        public static LdapTlsProvider? Create(LdapWireOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            if (options.ServerCertificate is { } supplied)
                return new LdapTlsProvider(RequirePrivateKey(supplied, "the certificate supplied in ServerCertificate"));

            if (string.IsNullOrWhiteSpace(options.TlsCertificatePath))
                return null;

            X509Certificate2 loaded;
            try
            {
#if NET9_0_OR_GREATER
                loaded = X509CertificateLoader.LoadPkcs12FromFile(options.TlsCertificatePath, options.TlsCertificatePassword);
#else
                loaded = new X509Certificate2(options.TlsCertificatePath, options.TlsCertificatePassword);
#endif
            }
            catch (Exception ex)
            {
                throw new LdapConfigurationException(
                    $"The ldap TLS certificate at '{options.TlsCertificatePath}' could not be loaded. It must be a "
                    + "PKCS#12 (.pfx) file containing the private key, and TlsCertificatePassword must match. "
                    + "Refusing to start: an unusable certificate must never degrade the listener to cleartext.",
                    ex);
            }
            return new LdapTlsProvider(RequirePrivateKey(loaded, $"the certificate at '{options.TlsCertificatePath}'"));
        }

        private static X509Certificate2 RequirePrivateKey(X509Certificate2 certificate, string description)
        {
            if (!certificate.HasPrivateKey)
                throw new LdapConfigurationException(
                    $"The ldap TLS certificate is unusable: {description} carries no private key, so no TLS "
                    + "handshake can complete. Refusing to start.");
            return certificate;
        }

        /// <summary>
        /// Completes the server half of a TLS handshake over <paramref name="inner"/> and returns the
        /// confidential stream. The handshake is bounded by <paramref name="handshakeTimeout"/>: the
        /// admission slot is already held at this point, so a peer that opens a socket and then stalls
        /// mid-handshake must not be able to hold it indefinitely. The inner stream is left open — the
        /// connection handler that created the transport owns its lifetime.
        /// </summary>
        public async Task<SslStream> AuthenticateAsync(Stream inner, TimeSpan handshakeTimeout, CancellationToken ct)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(handshakeTimeout);

            var ssl = new SslStream(inner, leaveInnerStreamOpen: true);
            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }, deadline.Token);
            }
            catch
            {
                await ssl.DisposeAsync();
                throw;
            }
            return ssl;
        }
    }
}
