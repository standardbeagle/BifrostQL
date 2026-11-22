using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server.Grpc;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Xunit;

namespace BifrostQL.Server.Test.Grpc
{
    /// <summary>
    /// Kestrel runs one request pipeline for every listener, so the gRPC routes used to answer on
    /// any host listener that spoke HTTP/2 (bypassing the gRPC port's loopback bind, TLS requirement
    /// and admission cap), and the gRPC port served the host's whole pipeline. These run REAL
    /// sockets: TestServer has no listener, so it cannot observe which port accepted a request.
    /// </summary>
    public sealed class GrpcListenerIsolationTests
    {
        private const string GetWidgets = "/bifrostql.BifrostQuery/GetWidgets";
        private const string Reflection = "/grpc.reflection.v1alpha.ServerReflection/ServerReflectionInfo";

        [Theory]
        [InlineData(GetWidgets)]
        [InlineData(Reflection)]
        public async Task Grpc_routes_do_not_answer_on_a_host_listener(string path)
        {
            var (hostPort, grpcPort) = (FreePort(), FreePort());
            using var host = await StartAsync(hostPort, grpcPort);

            var onHost = await PostGrpcAsync(hostPort, path);
            var onGrpc = await PostGrpcAsync(grpcPort, path);

            onHost.StatusCode.Should().Be(HttpStatusCode.NotFound,
                "a gRPC route reached through the host listener bypasses the gRPC port's bind, TLS and admission cap");
            onHost.Headers.Contains("grpc-status").Should().BeFalse("no gRPC handler may run on the host listener");
            // Control: the same call on the gRPC listener reaches the gRPC stack.
            onGrpc.StatusCode.Should().Be(HttpStatusCode.OK);
            onGrpc.Headers.Contains("grpc-status").Should().BeTrue();
        }

        [Fact]
        public async Task Host_pipeline_does_not_answer_on_the_grpc_listener()
        {
            var (hostPort, grpcPort) = (FreePort(), FreePort());
            using var host = await StartAsync(hostPort, grpcPort);

            var onGrpc = await GetAsync(grpcPort, "/graphql");
            var onHost = await GetAsync(hostPort, "/graphql");

            onGrpc.StatusCode.Should().Be(HttpStatusCode.NotFound,
                "the gRPC port's posture was declared for gRPC alone, not for /graphql, OData or S3");
            (await onGrpc.Content.ReadAsStringAsync()).Should().NotContain("graphql-served");
            // Control: the host listener still serves the host pipeline.
            onHost.StatusCode.Should().Be(HttpStatusCode.OK);
            (await onHost.Content.ReadAsStringAsync()).Should().Be("graphql-served");
        }

        [Fact]
        public async Task Host_urls_still_bind_when_the_grpc_listener_is_added()
        {
            // The gRPC listener is declared in code (KestrelServerOptions.Listen). Kestrel treats
            // code-declared endpoints as replacing the hosting URLs (UseUrls, ASPNETCORE_URLS,
            // --urls), so registering the adapter silently unbound the host's own HTTP port.
            // The hosting URL asks for port 0, so Kestrel picks the port at bind time and no other
            // test can take it between a probe and the bind. The gRPC port must be known up front
            // (the isolation keys on it), so only that one is probed.
            var grpcPort = FreePort();
            using var host = await StartAsync(hostPort: 0, grpcPort, hostListenerViaUrls: true);

            // Kestrel publishes every endpoint it actually bound. Without the carry-over only the
            // gRPC listener is there: the hosting URL was dropped.
            var bound = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
                .Select(a => BindingAddress.Parse(a).Port)
                .ToList();
            bound.Should().Contain(grpcPort);
            var hostPort = bound.Where(p => p != grpcPort).Should().ContainSingle(
                "the hosting URL must still bind beside the code-declared gRPC listener").Which;

            using var http1 = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var response = await http1.GetAsync($"http://127.0.0.1:{hostPort}/graphql");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync()).Should().Be("graphql-served");
            // ...and the gRPC listener is still there beside it.
            (await PostGrpcAsync(grpcPort, GetWidgets)).Headers.Contains("grpc-status").Should().BeTrue();
        }

        // ---- fixtures --------------------------------------------------------

        private static async Task<IHost> StartAsync(int hostPort, int grpcPort, bool hostListenerViaUrls = false)
        {
            var model = Task.FromResult(Model());
            var executor = Substitute.For<IQueryIntentExecutor>();
            executor.GetModelAsync(Arg.Any<string?>()).Returns(model);

            var builder = WebApplication.CreateBuilder();
            if (hostListenerViaUrls)
            {
                builder.WebHost.UseKestrel();
                builder.WebHost.UseUrls($"http://127.0.0.1:{hostPort}");
            }
            else
            {
                builder.WebHost.UseKestrel(k =>
                    // A code-declared host listener speaking h2c, standing in for a public
                    // HTTPS port that negotiates h2 through ALPN.
                    k.Listen(IPAddress.Loopback, hostPort, l => l.Protocols = HttpProtocols.Http2));
                builder.WebHost.UseUrls();
            }
            builder.Services.AddSingleton(executor);
            builder.Services.AddSingleton(Substitute.For<IMutationIntentExecutor>());
            builder.Services.AddBifrostGrpc(o => o.Port = grpcPort);

            var app = builder.Build();
            app.UseRouting();
            // Stands in for UseBifrostQL(): a host middleware serving /graphql.
            app.Use(async (http, next) =>
            {
                if (http.Request.Path == "/graphql")
                {
                    await http.Response.WriteAsync("graphql-served");
                    return;
                }
                await next(http);
            });
            await app.MapBifrostGrpcAsync();
            await app.StartAsync();
            return app;
        }

        private static HttpClient H2Client() => new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) })
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = TimeSpan.FromSeconds(10),
        };

        private static async Task<HttpResponseMessage> PostGrpcAsync(int port, string path)
        {
            using var client = H2Client();
            // An empty, uncompressed gRPC frame: [compressed=0][length=0].
            var content = new ByteArrayContent(new byte[5]);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
            var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}{path}")
            {
                Content = content,
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            request.Headers.TryAddWithoutValidation("te", "trailers");
            var response = await client.SendAsync(request);
            await response.Content.ReadAsByteArrayAsync();
            return response;
        }

        private static async Task<HttpResponseMessage> GetAsync(int port, string path)
        {
            using var client = H2Client();
            var response = await client.GetAsync($"http://127.0.0.1:{port}{path}");
            await response.Content.LoadIntoBufferAsync();
            return response;
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

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
