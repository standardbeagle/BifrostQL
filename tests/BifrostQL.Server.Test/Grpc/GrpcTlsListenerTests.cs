using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server.Grpc;
using BifrostQL.Tests.Shared;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Xunit;

namespace BifrostQL.Server.Test.Grpc
{
    /// <summary>
    /// <c>RequireTls</c> must configure the listener, not merely be validated. These run a REAL
    /// Kestrel listener (the TestServer harness has no socket, so it cannot see this at all) and
    /// assert the observable property an operator is promised: with <c>RequireTls</c> set, the port
    /// speaks TLS and REFUSES cleartext h2c — so a bearer credential cannot cross the wire in the
    /// clear while startup logs "TLS: True".
    /// </summary>
    public sealed class GrpcTlsListenerTests
    {
        private const string CertPassword = "bifrost-test";

        [Fact]
        public async Task RequireTls_makes_the_port_speak_tls_and_refuse_cleartext()
        {
            var certPath = WriteSelfSignedPfx();
            try
            {
                var (host, port) = await StartOnAProbedPortAsync(o =>
                {
                    o.RequireTls = true;
                    o.TlsCertificatePath = certPath;
                    o.TlsCertificatePassword = CertPassword;
                });
                using var _ = host;

                // Cleartext h2c must NOT be served: a credential sent here would be readable by
                // anyone on the path.
                var cleartext = await Record.ExceptionAsync(() => GetAsync($"http://127.0.0.1:{port}/", tls: false));
                cleartext.Should().NotBeNull(
                    "a TLS-required port must refuse cleartext h2c — otherwise RequireTls is inert and "
                    + "every bearer credential crosses the wire in the clear");

                // …and the TLS handshake must complete against the configured certificate.
                var overTls = await Record.ExceptionAsync(() => GetAsync($"https://127.0.0.1:{port}/", tls: true));
                overTls.Should().BeNull("the configured certificate must actually be served");
            }
            finally
            {
                File.Delete(certPath);
            }
        }

        [Fact]
        public async Task RequireTls_with_an_unreadable_certificate_aborts_startup()
        {
            // A certificate that cannot be loaded (wrong password) must FAIL CLOSED at startup with
            // an actionable error — never silently fall back to cleartext.
            var certPath = WriteSelfSignedPfx();
            try
            {
                // The certificate load fails before Kestrel binds, so no port race is in play.
                var act = () => StartAsync(o =>
                {
                    o.Port = LoopbackPort.Probe();
                    o.RequireTls = true;
                    o.TlsCertificatePath = certPath;
                    o.TlsCertificatePassword = "not-the-password";
                });

                (await act.Should().ThrowAsync<GrpcConfigurationException>())
                    .WithMessage("*TLS certificate*");
            }
            finally
            {
                File.Delete(certPath);
            }
        }

        [Fact]
        public async Task Without_RequireTls_the_port_stays_cleartext_h2c()
        {
            // The documented default. Pinned so the fix above cannot silently turn every existing
            // h2c deployment into a TLS port.
            var (host, port) = await StartOnAProbedPortAsync(o => o.RequireTls = false);
            using var _ = host;

            (await Record.ExceptionAsync(() => GetAsync($"http://127.0.0.1:{port}/", tls: false)))
                .Should().BeNull();
        }

        // ---- fixtures --------------------------------------------------------

        /// <summary>
        /// The gRPC adapter needs its port before start (it validates 1..65535), so the port is
        /// probed and a lost probe-then-bind race is retried on a fresh one.
        /// </summary>
        private static Task<(IHost Host, int Port)> StartOnAProbedPortAsync(Action<GrpcWireOptions> configure)
            => LoopbackPort.StartWithRetryAsync(async () =>
            {
                var port = LoopbackPort.Probe();
                return (await StartAsync(o =>
                {
                    o.Port = port;
                    configure(o);
                }), port);
            });

        private static async Task<IHost> StartAsync(Action<GrpcWireOptions> configure)
        {
            // The model is built BEFORE the Returns(...) call: creating substitutes inside the
            // argument would clobber NSubstitute's pending-call state.
            var model = Task.FromResult(Model());
            var executor = Substitute.For<IQueryIntentExecutor>();
            executor.GetModelAsync(Arg.Any<string?>()).Returns(model);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseKestrel();
            // Kestrel binds only what AddBifrostGrpc configures; no default URL.
            builder.WebHost.UseUrls();
            builder.Services.AddSingleton(executor);
            builder.Services.AddBifrostGrpc(configure);

            var app = builder.Build();
            app.UseRouting();
            await app.MapBifrostGrpcAsync();
            await app.StartAsync();
            return app;
        }

        /// <summary>An HTTP/2 request with prior knowledge, so cleartext h2c is not negotiated away.</summary>
        private static async Task GetAsync(string url, bool tls)
        {
            var handler = new SocketsHttpHandler
            {
                // A TLS handshake against a port that is NOT speaking TLS stalls indefinitely —
                // SslStream waits for a ServerHello the h2c listener will never send, and neither
                // HttpClient.Timeout nor ConnectTimeout bounds it. The explicit token below is what
                // actually caps this test; without it the mismatch hangs instead of failing.
                ConnectTimeout = TimeSpan.FromSeconds(5),
            };
            if (tls)
                handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;

            using var client = new HttpClient(handler)
            {
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            // Any HTTP status proves the transport worked; only a transport fault throws.
            await client.GetAsync(url, deadline.Token);
        }

        private static string WriteSelfSignedPfx()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=bifrost-grpc-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

            var path = Path.Combine(Path.GetTempPath(), $"bifrost-grpc-{Guid.NewGuid():N}.pfx");
            File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx, CertPassword));
            return path;
        }

        // Mirrors the GrpcAdapterStartupTests fixtures — the descriptor build only needs a
        // single PK-bearing table to succeed at startup.
        private static IDbModel Model()
        {
            var table = Substitute.For<IDbTable>();
            table.GraphQlName.Returns("Widgets");
            table.DbName.Returns("Widgets");
            table.TableSchema.Returns("dbo");
            table.Columns.Returns(new[]
            {
                new ColumnDto { ColumnName = "id", GraphQlName = "id", DataType = "int", IsPrimaryKey = true },
            });
            table.GetMetadataValue(MetadataKeys.Policy.Actions).Returns((string?)null);
            table.GetMetadataValue(MetadataKeys.Policy.ReadDeny).Returns((string?)null);

            var model = Substitute.For<IDbModel>();
            model.Tables.Returns(new[] { table });
            return model;
        }
    }
}
