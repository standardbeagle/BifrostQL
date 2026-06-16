using System.Text;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using GraphQL.SystemTextJson;
using GraphQL.Types;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// End-to-end proof of per-profile schema resolution through the real
/// <see cref="BifrostHttpMiddleware"/>: a registered "poly" profile carries a
/// polymorphic-notes overlay (Metadata + Modules=["polymorphic"]) and exposes a
/// <c>notes</c> join on <c>companies</c>; a request with no profile resolves to the
/// raw default schema where that join is absent. This proves the middleware reads
/// the schema from the per-profile <see cref="ProfileModelCache"/> and that the raw
/// default does not leak opt-in, metadata-driven joins.
/// </summary>
[Collection("PerProfileSchemaResolution")]
public sealed class PerProfileSchemaResolutionTests : IAsyncLifetime
{
    private const string GraphQlPath = "/graphql";
    private const string ProfileName = "poly";

    // The shared notes table carries the discriminator columns but no polymorphic
    // metadata in the base read; only the "poly" profile overlays the rule.
    private static readonly string[] PolyMetadata =
    {
        "*.notes { polymorphic-type-column: entity_type; polymorphic-id-column: entity_id; polymorphic-map: company=companies }",
    };

    private string _connectionString = null!;
    private SqliteConnection _keepAlive = null!;
    private SqliteDbConnFactory _connFactory = null!;
    private ProfileModelCache _profileCache = null!;
    private BifrostProfileRegistry _profileRegistry = null!;

    public async Task InitializeAsync()
    {
        _connectionString = $"Data Source=bifrost_per_profile_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
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
                );
                CREATE TABLE notes (
                    note_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    entity_type TEXT NOT NULL,
                    entity_id INTEGER NOT NULL,
                    content TEXT NOT NULL
                );", conn);
            await ddl.ExecuteNonQueryAsync();

            var seed = new SqliteCommand(
                @"INSERT INTO companies (company_id, name) VALUES (1, 'Acme');
                  INSERT INTO notes (entity_type, entity_id, content) VALUES ('company', 1, 'first');", conn);
            await seed.ExecuteNonQueryAsync();
        }

        _profileRegistry = new BifrostProfileRegistry();
        _profileRegistry.Add(new BifrostProfile
        {
            Name = ProfileName,
            Modules = new[] { "polymorphic" },
            Metadata = PolyMetadata,
        });

        var loader = new DbModelLoader(_connFactory, new MetadataLoader(Array.Empty<string>()));
        var read = await loader.ReadAsync();
        _profileCache = new ProfileModelCache(
            loader, read, Array.Empty<string>(), additionalMetadata: null, registry: _profileRegistry);
    }

    public async Task DisposeAsync()
    {
        await _keepAlive.DisposeAsync();
    }

    private ServiceProvider BuildRequestServices()
    {
        var filterTransformers = new FilterTransformersWrap
        {
            Transformers = Array.Empty<IFilterTransformer>(),
        };

        var pathCache = new PathCache<Inputs>();
        var (model, schema) = _profileCache.GetFor(null);
        pathCache.AddLoader(GraphQlPath, () => Task.FromResult(new Inputs(new Dictionary<string, object?>
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
        services.AddSingleton(pathCache);
        services.AddSingleton(_profileRegistry);
        services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Runs an introspection query for the fields of the <c>companies</c> type
    /// through the middleware under the given (optional) profile and returns the
    /// set of field names exposed on that type.
    /// </summary>
    private async Task<HashSet<string>> CompaniesFieldsAsync(string? profile)
    {
        const string introspection =
            "query { __type(name: \"companies\") { fields { name } } }";

        var serializer = new GraphQLSerializer();
        var middleware = new BifrostHttpMiddleware(
            next: _ => Task.CompletedTask,
            serializer: serializer,
            documentExecutor: new DocumentExecuter(),
            logger: NullLogger<BifrostHttpMiddleware>.Instance);

        await using var provider = BuildRequestServices();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.RequestServices.GetRequiredService<IHttpContextAccessor>().HttpContext = context;

        context.Request.Method = HttpMethods.Post;
        // Mirror production routing: app.Map("/graphql") moves the matched prefix to
        // PathBase and leaves Path empty, so the path-segment profile fallback in
        // ResolveProfileName does not treat the endpoint path as a profile name.
        context.Request.PathBase = GraphQlPath;
        context.Request.Path = PathString.Empty;
        if (profile != null)
            context.Request.QueryString = new QueryString($"?profile={profile}");
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { query = introspection })));
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(200);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var json = await reader.ReadToEndAsync();

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("errors", out _).Should().BeFalse($"introspection should not error: {json}");
        var fields = doc.RootElement
            .GetProperty("data")
            .GetProperty("__type")
            .GetProperty("fields")
            .EnumerateArray()
            .Select(f => f.GetProperty("name").GetString()!)
            .ToHashSet();
        return fields;
    }

    [Fact]
    public async Task PolyProfile_ExposesNotesJoinOnCompanies()
    {
        var fields = await CompaniesFieldsAsync(ProfileName);

        fields.Should().Contain("notes",
            "the poly profile's polymorphic overlay surfaces notes as a join on companies");
    }

    [Fact]
    public async Task NoProfile_DoesNotExposeNotesJoinOnCompanies()
    {
        var fields = await CompaniesFieldsAsync(profile: null);

        fields.Should().NotContain("notes",
            "the raw default profile carries no polymorphic overlay, so no notes join leaks");
    }

    [Fact]
    public async Task DefaultProfile_DoesNotExposeNotesJoinOnCompanies()
    {
        var fields = await CompaniesFieldsAsync(profile: "default");

        fields.Should().NotContain("notes",
            "an explicit default profile is also raw and exposes no polymorphic join");
    }
}
