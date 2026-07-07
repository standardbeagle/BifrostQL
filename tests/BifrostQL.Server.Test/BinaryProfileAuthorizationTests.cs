using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// SEC-MED-2 regression: the binary WebSocket transport must enforce the same profile
    /// role gating and per-profile module filtering as the HTTP path. That enforcement
    /// lives in the shared <see cref="BifrostEngine"/>, which <see cref="BifrostBinaryMiddleware"/>
    /// invokes for every binary Query/Mutation (via <c>IBifrostEngine.ExecuteAsync</c>).
    /// Before the fix the engine read the base schema and singleton transformer service
    /// directly and never consulted the profile, so a binary caller could bypass
    /// <c>RequireRole</c> and — through the fail-closed transformer filter — tenant
    /// isolation / soft-delete. These tests drive the engine exactly as the middleware
    /// does, resolving the request's HttpContext through <see cref="IHttpContextAccessor"/>
    /// (populated by ASP.NET Core per request, including WebSocket upgrades).
    /// </summary>
    public sealed class BinaryProfileAuthorizationTests : IAsyncLifetime
    {
        private const string EndpointPath = "/bifrost-ws";
        private const string AdminProfile = "admin";

        private string _connectionString = null!;
        private SqliteConnection _keepAlive = null!;
        private SqliteDbConnFactory _connFactory = null!;
        private ProfileModelCache _profileCache = null!;
        private BifrostProfileRegistry _profileRegistry = null!;

        public async Task InitializeAsync()
        {
            _connectionString = $"Data Source=bifrost_binary_authz_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            _keepAlive = new SqliteConnection(_connectionString);
            await _keepAlive.OpenAsync();

            _connFactory = new SqliteDbConnFactory(_connectionString);

            await using (var conn = new SqliteConnection(_connectionString))
            {
                await conn.OpenAsync();
                var ddl = new SqliteCommand(
                    @"CREATE TABLE companies (
                        company_id INTEGER PRIMARY KEY AUTOINCREMENT,
                        name TEXT NOT NULL
                    );", conn);
                await ddl.ExecuteNonQueryAsync();
            }

            _profileRegistry = new BifrostProfileRegistry();
            // A role-gated profile: only callers in the "Admin" role may select it.
            _profileRegistry.Add(new BifrostProfile { Name = AdminProfile, RequireRole = "Admin" });

            var loader = new DbModelLoader(_connFactory, new MetadataLoader(Array.Empty<string>()));
            var read = await loader.ReadAsync();
            _profileCache = new ProfileModelCache(
                loader, read, Array.Empty<string>(), additionalMetadata: null, registry: _profileRegistry);
        }

        public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

        private ServiceProvider BuildProvider()
        {
            var filterTransformers = new FilterTransformersWrap
            {
                Transformers = Array.Empty<IFilterTransformer>(),
            };

            var pathCache = new PathCache<Inputs>();
            var (model, schema) = _profileCache.GetFor(null);
            pathCache.AddLoader(EndpointPath, () => Task.FromResult(new Inputs(new Dictionary<string, object?>
            {
                { "connFactory", _connFactory },
                { "model", model },
                { "dbSchema", schema },
                { "profileModelCache", _profileCache },
            })));

            var services = new ServiceCollection();
            services.AddSingleton<IFilterTransformers>(filterTransformers);
            services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap
            {
                Transformers = Array.Empty<IMutationTransformer>(),
            });
            services.AddSingleton<IQueryTransformerService>(new QueryTransformerService(filterTransformers));
            services.AddSingleton<IQueryObservers>(new QueryObserversWrap());
            services.AddSingleton(pathCache);
            services.AddSingleton(_profileRegistry);
            services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
            services.AddSingleton<IDocumentExecuter>(new DocumentExecuter());
            services.AddBifrostEngine();
            return services.BuildServiceProvider();
        }

        /// <summary>
        /// Executes "{ __typename }" through the real engine the way the binary middleware
        /// would: the request's HttpContext (carrying the profile header and any
        /// authenticated principal) is exposed via IHttpContextAccessor, and the request
        /// flows in with the same RequestServices the middleware passes.
        /// </summary>
        private async Task<BifrostResult> RunEngineAsync(string? profile, params string[] roles)
        {
            await using var provider = BuildProvider();
            var engine = provider.GetRequiredService<IBifrostEngine>();

            var context = new DefaultHttpContext { RequestServices = provider };
            if (profile != null)
                context.Request.Headers["X-BifrostQL-Profile"] = profile;
            if (roles.Length > 0)
            {
                var identity = new ClaimsIdentity(authenticationType: "test");
                foreach (var role in roles)
                    identity.AddClaim(new Claim(ClaimTypes.Role, role));
                context.User = new ClaimsPrincipal(identity);
            }
            provider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;

            return await engine.ExecuteAsync(new BifrostRequest
            {
                Query = "{ __typename }",
                UserContext = new Dictionary<string, object?>(),
                RequestServices = provider,
                CancellationToken = default,
            }, EndpointPath);
        }

        private static IEnumerable<string> Messages(BifrostResult result)
            => result.Errors.Select(e => e.Message);

        [Fact]
        public async Task RoleGatedProfile_Unauthenticated_IsRejected()
        {
            var result = await RunEngineAsync(AdminProfile);

            Messages(result).Should().ContainSingle()
                .Which.Should().Contain("requires authentication",
                    "the binary engine must enforce the profile's RequireRole like the HTTP path");
        }

        [Fact]
        public async Task RoleGatedProfile_WrongRole_IsRejected()
        {
            var result = await RunEngineAsync(AdminProfile, "Viewer");

            Messages(result).Should().ContainSingle()
                .Which.Should().Contain("requires role");
        }

        [Fact]
        public async Task RoleGatedProfile_WithRequiredRole_PassesTheGate()
        {
            var result = await RunEngineAsync(AdminProfile, "Admin");

            Messages(result).Should().NotContain(m => m.Contains("requires"),
                "a caller holding the required role passes the profile gate");
        }

        [Fact]
        public async Task UnknownProfile_IsRejected()
        {
            var result = await RunEngineAsync("does-not-exist");

            Messages(result).Should().ContainSingle()
                .Which.Should().Contain("Unknown profile");
        }

        [Fact]
        public async Task NoProfile_DefaultProfile_ExecutesWithoutAuthorizationError()
        {
            var result = await RunEngineAsync(profile: null);

            Messages(result).Should().NotContain(m => m.Contains("requires") || m.Contains("Unknown profile"),
                "the default profile requires no role");
        }
    }
}
