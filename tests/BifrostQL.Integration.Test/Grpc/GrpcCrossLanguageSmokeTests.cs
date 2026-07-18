using System.Diagnostics;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Model;
using BifrostQL.Server;
using BifrostQL.Server.Grpc;
using BifrostQL.Sqlite;
using FluentAssertions;
using Google.Protobuf.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Integration.Test.Grpc
{
    /// <summary>
    /// Slice-7 criterion 3 — the generated protos must COMPILE and execute a smoke call, in C# PLUS one
    /// non-.NET toolchain, with HONEST reporting of what actually ran versus what skipped.
    ///
    /// <para><b>C# smoke (always runs).</b> The reflection endpoint's descriptor set is parsed into a
    /// <see cref="FileDescriptorSet"/> — the exact artifact a code generator (protoc, grpcurl, buf)
    /// consumes — proving the generated schema COMPILES to a valid descriptor, and a real Get executes
    /// over the wire. No external toolchain, no network.</para>
    ///
    /// <para><b>Cross-language smoke (grpcurl — gated, skips honestly when absent).</b> A grpcurl
    /// binary is a genuinely non-.NET gRPC client (Go). When it is on PATH, the test stands up a REAL
    /// Kestrel HTTP/2 (h2c) listener on a loopback port and drives grpcurl against the reflection
    /// endpoint — a true cross-language interop assertion. When grpcurl is ABSENT (the common CI/dev
    /// case), the test is a DOCUMENTED, VISIBLE skip ("grpcurl not on PATH") — never a silently-passing
    /// fake. Prerequisite to run it locally: install grpcurl
    /// (https://github.com/fullstorydev/grpcurl) so it resolves on PATH. This honors the honest-smoke
    /// discipline: presence → real assertion, absence → visible skip, and the skip is NEVER counted as a
    /// pass.</para>
    /// </summary>
    public sealed class GrpcCrossLanguageSmokeTests
    {
        private static readonly string[] MetadataRules = { "*.widgets { }" };

        private static readonly string[] SeedSql =
        {
            "DROP TABLE IF EXISTS widgets",
            "CREATE TABLE widgets (id INTEGER PRIMARY KEY, name TEXT NOT NULL)",
            "INSERT INTO widgets(id, name) VALUES (1,'first'),(2,'second')",
        };

        [Fact]
        public async Task Generated_descriptor_set_compiles_and_a_csharp_smoke_call_executes()
        {
            await using var fixture = await GrpcLoopbackFixture.StartAsync(
                nameof(Generated_descriptor_set_compiles_and_a_csharp_smoke_call_executes), MetadataRules, SeedSql);
            var client = new GrpcWireClient(fixture.Invoker, fixture.Contract);

            // The generated schema COMPILES: its descriptor set parses as a valid proto3 FileDescriptorSet
            // with the service + row message a downstream toolchain would generate stubs from.
            var descriptor = GrpcDescriptorSetWriter.Write(fixture.Contract);
            var set = FileDescriptorSet.Parser.ParseFrom(descriptor);
            var file = set.File.Single(f => f.Name == "bifrostql.proto");
            file.Syntax.Should().Be("proto3");
            file.Service.Single().Name.Should().Be("BifrostQuery");
            file.MessageType.Select(m => m.Name).Should().Contain("widgetsRow");

            // And a real smoke call round-trips over the wire.
            var row = await client.GetAsync(
                "widgets", new Dictionary<string, object?> { ["id"] = 1 }, GrpcLoopbackFixture.Identity("u", roles: "member"));
            row!["name"].Should().Be("first");
        }

        [SkippableFact]
        public async Task Cross_language_smoke_via_grpcurl_lists_the_service_over_a_real_socket()
        {
            Skip.IfNot(GrpcurlAvailable(), "grpcurl not on PATH — cross-language smoke skipped (install github.com/fullstorydev/grpcurl to run).");

            await using var host = await RealKestrelHost.StartAsync(MetadataRules, SeedSql);

            var (exit, stdout, stderr) = await RunProcessAsync(
                "grpcurl", $"-plaintext -connect-timeout 5 {host.Address} list");

            exit.Should().Be(0, $"grpcurl should list services; stderr: {stderr}");
            stdout.Should().Contain("bifrostql.BifrostQuery", "the non-.NET client must discover the generated service via reflection");
        }

        private static bool GrpcurlAvailable()
        {
            try
            {
                var (exit, _, _) = RunProcessAsync("grpcurl", "--version").GetAwaiter().GetResult();
                return exit == 0;
            }
            catch (Exception)
            {
                return false; // binary not found / not executable → not available
            }
        }

        private static async Task<(int Exit, string Stdout, string Stderr)> RunProcessAsync(string file, string args)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = file,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, stdout, stderr);
        }

        /// <summary>
        /// A real Kestrel HTTP/2 (h2c) host on an ephemeral loopback port — the only shape an external
        /// process (grpcurl) can connect to. Only built when the cross-language smoke actually runs.
        /// </summary>
        private sealed class RealKestrelHost : IAsyncDisposable
        {
            private readonly WebApplication _app;
            public string Address { get; }

            private RealKestrelHost(WebApplication app, string address)
            {
                _app = app;
                Address = address;
            }

            public static async Task<RealKestrelHost> StartAsync(string[] metadataRules, string[] seedSql)
            {
                var connString = $"Data Source=grpc_grpcurl_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
                var keepAlive = new SqliteConnection(connString);
                await keepAlive.OpenAsync();
                foreach (var sql in seedSql)
                {
                    await using var cmd = new SqliteCommand(sql, keepAlive);
                    await cmd.ExecuteNonQueryAsync();
                }

                DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
                var builder = WebApplication.CreateBuilder();
                builder.WebHost.UseSetting("urls", ""); // the adapter's listener is the sole endpoint
                builder.WebHost.ConfigureKestrel(k =>
                    k.ListenLocalhost(0, o => o.Protocols = HttpProtocols.Http2)); // ephemeral h2c loopback
                builder.Services.AddBifrostEndpoints(o =>
                    o.AddEndpoint(e =>
                    {
                        e.ConnectionString = connString;
                        e.Provider = "sqlite";
                        e.Path = GrpcLoopbackFixture.EndpointPath;
                        e.Metadata = metadataRules;
                        e.DisableAuth = true;
                    }));
                builder.Services.AddSingleton<IBifrostAuthContextFactory, HeaderIdentityFactory>();
                builder.Services.AddBifrostGrpc(o =>
                {
                    o.Endpoint = GrpcLoopbackFixture.EndpointPath;
                    o.Port = 0; // let the shared listener bind an ephemeral port too; discovered below
                });

                var app = builder.Build();
                app.UseRouting();
                app.MapBifrostGrpc();
                await app.StartAsync();

                var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
                var url = addresses!.Addresses.First();
                var authority = new Uri(url).Authority;
                _ = keepAlive; // kept open for the app lifetime by the shared-cache connection above
                return new RealKestrelHost(app, authority);
            }

            public async ValueTask DisposeAsync()
            {
                await _app.StopAsync();
                await _app.DisposeAsync();
            }
        }
    }
}
