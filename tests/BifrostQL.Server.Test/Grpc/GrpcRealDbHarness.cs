using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server;
using BifrostQL.Server.Grpc;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using BifrostQL.Sqlite;

namespace BifrostQL.Server.Test.Grpc
{
    /// <summary>
    /// A real, transformer-pipeline gRPC fixture: a seeded in-memory SQLite database behind the full
    /// BifrostQL endpoint stack plus the opt-in gRPC front door, hosted in-process on a
    /// <see cref="TestServer"/> and driven by a real <see cref="GrpcChannel"/> over the test HTTP/2
    /// handler. RPCs travel the ACTUAL dynamic dispatch + reflection path into the real read pipeline,
    /// so tenant isolation, visibility filtering, and the generated SQL are exercised end to end —
    /// nothing is mocked. Mirrors <c>RespRealDbHarness</c>.
    ///
    /// <para>Identity arrives as gRPC metadata (<c>x-bifrost-test-identity</c> = <c>user|tenant|roles</c>)
    /// projected by <see cref="HeaderTestAuthContextFactory"/>, standing in for slice-4 bearer identity.
    /// A call with NO such metadata resolves to an EMPTY user context — the real fail-closed path.</para>
    /// </summary>
    internal sealed class GrpcRealDbHarness : IAsyncDisposable
    {
        public const string EndpointPath = "/graphql";
        public const string IdentityHeader = "x-bifrost-test-identity";

        private readonly string _connString;
        private readonly string[] _metadataRules;
        private readonly string[] _seedSql;
        private readonly GrpcWireOptions _grpcOptions;
        private readonly SqlCaptureObserver _sqlCapture = new();
        private SqliteConnection _keepAlive = null!;
        private IHost _host = null!;
        private GrpcChannel _channel = null!;

        public GrpcContract Contract { get; private set; } = null!;

        private GrpcRealDbHarness(string connName, string[] metadataRules, string[] seedSql, GrpcWireOptions grpcOptions)
        {
            _connString = $"Data Source=grpc_{connName};Mode=Memory;Cache=Shared";
            _metadataRules = metadataRules;
            _seedSql = seedSql;
            _grpcOptions = grpcOptions;
        }

        public static async Task<GrpcRealDbHarness> StartAsync(
            string connName, string[] metadataRules, string[] seedSql, GrpcWireOptions? grpcOptions = null)
        {
            var options = grpcOptions ?? new GrpcWireOptions();
            options.Endpoint = EndpointPath;
            var harness = new GrpcRealDbHarness(connName, metadataRules, seedSql, options);
            await harness.InitializeAsync();
            return harness;
        }

        private async Task InitializeAsync()
        {
            _keepAlive = new SqliteConnection(_connString);
            await _keepAlive.OpenAsync();
            foreach (var sql in _seedSql)
                await Exec(sql);

            DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
            var builder = new HostBuilder().ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddBifrostEndpoints(o =>
                    {
                        o.AddEndpoint(e =>
                        {
                            e.ConnectionString = _connString;
                            e.Provider = "sqlite";
                            e.Path = EndpointPath;
                            e.Metadata = _metadataRules;
                            e.DisableAuth = true;
                        });
                        o.AddQueryObservers(new IQueryObserver[] { _sqlCapture });
                    });
                    // Test identity projection (stands in for slice-4 bearer). Registered before
                    // AddBifrostGrpc so it wins the TryAdd of the default factory.
                    services.AddSingleton<IBifrostAuthContextFactory, HeaderTestAuthContextFactory>();
                    services.AddBifrostGrpc(o =>
                    {
                        o.Endpoint = _grpcOptions.Endpoint;
                        o.Port = _grpcOptions.Port;
                        o.MaxStreamRows = _grpcOptions.MaxStreamRows;
                        o.ListPageSize = _grpcOptions.ListPageSize;
                        o.RequireTls = _grpcOptions.RequireTls;
                        o.TlsCertificatePath = _grpcOptions.TlsCertificatePath;
                        o.EnableWrites = _grpcOptions.EnableWrites;
                    });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapBifrostGrpc());
                });
            });
            _host = await builder.StartAsync();

            var handler = _host.GetTestServer().CreateHandler();
            _channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler });

            // Build the client-side contract exactly as the server provider does (full projection,
            // empty manifest) so encode/decode uses the identical field numbers.
            var executor = _host.Services.GetRequiredService<IQueryIntentExecutor>();
            var model = await executor.GetModelAsync(EndpointPath);
            var visible = GrpcSchemaVisibility.ProjectAll(model);
            var manifest = GrpcFieldNumberManifest.Empty().Reconcile(visible);
            // Match the server's write posture so the client contract carries the mutation messages
            // (and their pinned field numbers) exactly when the server exposes them.
            Contract = GrpcSchemaGenerator.BuildContract(visible, manifest, _grpcOptions.EnableWrites);
        }

        public CallInvoker Invoker => _channel.CreateCallInvoker();
        public GrpcChannel Channel => _channel;

        public static Metadata Identity(string? user, string? tenant = null, string? roles = null)
        {
            var metadata = new Metadata();
            if (user is not null)
                metadata.Add(IdentityHeader, $"{user}|{tenant ?? string.Empty}|{roles ?? string.Empty}");
            return metadata;
        }

        public string CapturedSql(string table) => string.Join("\n---\n", _sqlCapture.SqlFor(table));

        private async Task Exec(string sql)
        {
            await using var cmd = new SqliteCommand(sql, _keepAlive);
            await cmd.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            _channel?.Dispose();
            if (_host is not null)
            {
                await _host.StopAsync();
                _host.Dispose();
            }
            if (_keepAlive is not null)
                await _keepAlive.DisposeAsync();
        }

        private sealed class SqlCaptureObserver : IQueryObserver
        {
            private readonly object _gate = new();
            private readonly List<(string Table, string Sql)> _captured = new();

            public QueryPhase[] Phases { get; } = { QueryPhase.AfterExecute };

            public ValueTask OnQueryPhaseAsync(QueryPhase phase, QueryObserverContext context)
            {
                lock (_gate)
                    _captured.Add((context.Table.DbName, context.Sql ?? string.Empty));
                return ValueTask.CompletedTask;
            }

            public IReadOnlyList<string> SqlFor(string table)
            {
                lock (_gate)
                    return _captured
                        .Where(c => string.Equals(c.Table, table, StringComparison.OrdinalIgnoreCase))
                        .Select(c => c.Sql)
                        .ToArray();
            }
        }
    }

    /// <summary>
    /// A test <see cref="IBifrostAuthContextFactory"/> that projects a <c>user|tenant|roles</c> gRPC
    /// metadata header into the same user-context keys the pipeline reads (<c>user_id</c>,
    /// <c>tenant_id</c>, <c>roles</c>). No header → an EMPTY context, exercising the real fail-closed
    /// path exactly as the production factory does for an unauthenticated principal.
    /// </summary>
    internal sealed class HeaderTestAuthContextFactory : IBifrostAuthContextFactory
    {
        /// <summary>Identity value that simulates a bearer from an OIDC issuer with no registered claim
        /// mapper — the production factory throws <c>UnmappedOidcIssuerException</c> here.</summary>
        public const string UnmappedIssuerSentinel = "__unmapped_issuer__";

        public IDictionary<string, object?> CreateUserContext(HttpContext context)
        {
            var raw = context.Request.Headers[GrpcRealDbHarness.IdentityHeader].ToString();
            if (string.IsNullOrEmpty(raw))
                return new Dictionary<string, object?>(); // fail-closed: no/malformed identity → empty

            if (raw == UnmappedIssuerSentinel)
                // Mirror the shared factory: an unmapped OIDC issuer fails closed by THROWING, never
                // degrading to an anonymous identity.
                throw new InvalidOperationException("Simulated unmapped OIDC issuer; rejecting.");

            var parts = raw.Split('|');
            var subject = parts[0];
            if (string.IsNullOrEmpty(subject))
                // Mirror BifrostContext.BuildAppIdentity: an authenticated principal with no subject
                // claim throws rather than collapse to anonymous.
                throw new InvalidOperationException("Authenticated principal has no subject claim.");

            var ctx = new Dictionary<string, object?> { ["user_id"] = subject };
            if (parts.Length > 1 && parts[1].Length > 0)
                ctx["tenant_id"] = parts[1];
            if (parts.Length > 2 && parts[2].Length > 0)
                ctx["roles"] = parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries);
            return ctx;
        }

        public IDictionary<string, object?> CreateUserContext(HttpContext context, IDictionary<string, object?> existing)
            => CreateUserContext(context);
    }
}
