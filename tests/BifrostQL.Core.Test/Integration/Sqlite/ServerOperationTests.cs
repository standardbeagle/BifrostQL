using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Operations;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using GraphQL.SystemTextJson;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// End-to-end proof of server operations (<see cref="IBifrostOperation"/>) on SQLite: the
/// fields are in the one schema (introspection, mixed requests), the declared policy runs
/// before the operation's code, the operation's data access is tenant-scoped and
/// audit-stamped as the caller, and a mutation operation commits or rolls back as a unit.
/// </summary>
public sealed class ServerOperationTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_server_operation_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;
    private readonly LineTotal _lineTotal = new();
    private readonly ImportLines _importLines = new();
    private readonly RecordingObserver _observer = new();

    private static readonly string[] Rules =
    {
        ":root { user-audit-key: user_id; operations: lineTotal, importLines[estimator,claims.import] }",
        "main.claims { tenant-filter: tenant_id }",
        "main.claim_lines { tenant-filter: tenant_id }",
        "main.claim_lines.created_by { populate: created-by }",
    };

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();
        await Exec("DROP TABLE IF EXISTS claim_lines");
        await Exec("DROP TABLE IF EXISTS claims");
        await Exec("CREATE TABLE claims (id INTEGER PRIMARY KEY, tenant_id INTEGER NOT NULL, title TEXT NOT NULL)");
        await Exec(
            """
            CREATE TABLE claim_lines (
                id         INTEGER PRIMARY KEY,
                tenant_id  INTEGER NOT NULL,
                claim_id   INTEGER NOT NULL REFERENCES claims(id),
                code       TEXT NOT NULL,
                units      INTEGER NOT NULL CHECK (units > 0),
                price      REAL NOT NULL,
                created_by TEXT NULL
            )
            """);
        await Exec("INSERT INTO claims(id, tenant_id, title) VALUES (1, 1, 'tenant one'), (2, 2, 'tenant two')");
        await Exec("INSERT INTO claim_lines(tenant_id, claim_id, code, units, price) VALUES (1, 1, 'A', 2, 10.0), (2, 2, 'B', 5, 100.0)");
        _model = await LoadModelAsync(Rules);
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    [Fact]
    public async Task Operations_AppearInIntrospection_WithTheirDeclaredTypes_AndUnlistedOnesDoNot()
    {
        var result = await ExecuteAsync(
            """
            {
              query: __type(name: "database") { fields { name args { name type { kind ofType { name } } } type { name } } }
              mutation: __type(name: "databaseInput") { fields { name type { kind ofType { name } } } }
              result: __type(name: "ImportResult") { fields { name } }
            }
            """, Estimator(1));

        result.Errors.Should().BeNullOrEmpty();
        using var doc = JsonDocument.Parse(new GraphQLSerializer().Serialize(result));
        var data = doc.RootElement.GetProperty("data");
        var lineTotal = data.GetProperty("query").GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("name").GetString() == "lineTotal");
        lineTotal.GetProperty("type").GetProperty("name").GetString().Should().Be("Float");
        var arg = lineTotal.GetProperty("args").EnumerateArray().Single();
        arg.GetProperty("name").GetString().Should().Be("claimId");
        arg.GetProperty("type").GetProperty("kind").GetString().Should().Be("NON_NULL");
        arg.GetProperty("type").GetProperty("ofType").GetProperty("name").GetString().Should().Be("Int");

        var mutationFields = data.GetProperty("mutation").GetProperty("fields").EnumerateArray()
            .Select(f => f.GetProperty("name").GetString()).ToList();
        mutationFields.Should().Contain("importLines").And.NotContain("unlisted");
        data.GetProperty("query").GetProperty("fields").EnumerateArray()
            .Select(f => f.GetProperty("name").GetString()).Should().NotContain("unlisted",
                "a registered operation that the endpoint does not list is not in the schema");
        data.GetProperty("result").GetProperty("fields").EnumerateArray()
            .Select(f => f.GetProperty("name").GetString()).Should().BeEquivalentTo("imported", "claimTitle");
    }

    [Fact]
    public async Task QueryOperation_SharesARequestWithGeneratedFields_AndReadsOnlyTheCallersTenant()
    {
        var own = await ExecuteAsync("{ lineTotal(claimId: 1) claims { data { id title } } }", Estimator(1));
        own.Errors.Should().BeNullOrEmpty();
        var json = new GraphQLSerializer().Serialize(own);
        json.Should().Contain("\"lineTotal\":20").And.Contain("tenant one").And.NotContain("tenant two");

        var other = await ExecuteAsync("{ lineTotal(claimId: 2) }", Estimator(1));
        other.Errors.Should().BeNullOrEmpty();
        new GraphQLSerializer().Serialize(other).Should().Contain("\"lineTotal\":0",
            "another tenant's lines are invisible to the operation's read, as to any generated read");
    }

    [Fact]
    public async Task MutationOperation_WritesAsTheCaller_PinnedToTheirTenant_AndAuditStamped()
    {
        var result = await ExecuteAsync(
            "mutation { importLines(claimId: 1, csv: \"X,1,5\\nY,3,7\") { imported claimTitle } }", Estimator(1));

        result.Errors.Should().BeNullOrEmpty();
        new GraphQLSerializer().Serialize(result).Should().Contain("\"imported\":2").And.Contain("tenant one");
        (await CountAsync("claim_lines", "claim_id = 1 AND tenant_id = 1 AND created_by = 'eve' AND code IN ('X','Y')"))
            .Should().Be(2);
        _observer.Tables.Should().Equal(new[] { "claim_lines", "claim_lines" },
            "post-commit observers see each committed write, after the operation commits");
    }

    [Fact]
    public async Task MutationOperation_FailingStep_RollsBackEveryEarlierWrite_AndNotifiesNoObserver()
    {
        // The third line violates CHECK (units > 0) after two inserts have run in the operation.
        var result = await ExecuteAsync(
            "mutation { importLines(claimId: 1, csv: \"X,1,5\\nY,3,7\\nZ,0,9\") { imported } }", Estimator(1));

        result.Errors.Should().ContainSingle();
        (await CountAsync("claim_lines", "code IN ('X','Y','Z')")).Should().Be(0,
            "the operation is one transaction: the two earlier inserts roll back with the failing one");
        _observer.Tables.Should().BeEmpty("no observer may describe a rolled-back write");
    }

    [Fact]
    public async Task MutationOperation_IntoAnotherTenantsClaim_SeesNoClaim_AndWritesNothing()
    {
        var result = await ExecuteAsync(
            "mutation { importLines(claimId: 2, csv: \"X,1,5\") { imported } }", Estimator(1));

        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("not available");
        (await CountAsync("claim_lines", "code = 'X'")).Should().Be(0);
    }

    [Fact]
    public async Task Policy_RefusesACallerWithoutTheGrant_OrWithoutATenant_BeforeTheOperationRuns()
    {
        var noGrant = new Dictionary<string, object?> { ["tenant_id"] = 1, ["user_id"] = "bob", ["roles"] = new[] { "viewer" } };
        var byPermission = new Dictionary<string, object?>
        {
            ["tenant_id"] = 1, ["user_id"] = "pat", ["permissions"] = new[] { "claims.import" },
        };
        var noTenant = new Dictionary<string, object?> { ["user_id"] = "eve", ["roles"] = new[] { "estimator" } };

        var refused = await ExecuteAsync("mutation { importLines(claimId: 1, csv: \"X,1,5\") { imported } }", noGrant);
        var tenantless = await ExecuteAsync("{ lineTotal(claimId: 1) }", noTenant);
        var permitted = await ExecuteAsync("mutation { importLines(claimId: 1, csv: \"P,1,5\") { imported } }", byPermission);

        refused.Errors.Should().ContainSingle().Which.Message.Should().Contain("may not run operation 'importLines'");
        tenantless.Errors.Should().ContainSingle().Which.Message.Should().Contain("requires a tenant context");
        _importLines.Runs.Should().Be(1, "only the permitted call reached the operation's code");
        _lineTotal.Runs.Should().Be(0);
        permitted.Errors.Should().BeNullOrEmpty("a permission named in the grant list admits the caller");
        (await CountAsync("claim_lines", "code = 'X'")).Should().Be(0);
    }

    [Fact]
    public async Task SchemaBuild_RefusesAListedButUnregisteredOperation_AndANameThatShadowsAGeneratedField()
    {
        var unregistered = await LoadModelAsync(new[] { ":root { operations: missingOp }" });
        var build = () => DbSchema.FromModel(unregistered, null, new IBifrostOperation[] { _lineTotal });
        build.Should().Throw<InvalidOperationException>().WithMessage("*missingOp*no server operation*");

        var shadowing = await LoadModelAsync(new[] { ":root { operations: claims }" });
        var shadow = () => DbSchema.FromModel(shadowing, null, new IBifrostOperation[] { new NamedOperation("claims") });
        shadow.Should().Throw<InvalidOperationException>().WithMessage("*claims*generated*");
    }

    [Fact]
    public async Task ModelLoad_RefusesAMalformedOperationsKey()
    {
        var empty = () => LoadModelAsync(new[] { ":root { operations: importLines[] }" });
        var tenant = () => LoadModelAsync(new[] { ":root { operations: lineTotal; operations-tenant: sometimes }" });

        await empty.Should().ThrowAsync<InvalidOperationException>().WithMessage("*operations*Malformed*");
        await tenant.Should().ThrowAsync<InvalidOperationException>().WithMessage("*operations-tenant*sometimes*");
    }

    // ---- operations under test ----

    private sealed class LineTotal : IBifrostOperation
    {
        public int Runs;
        public string Name => "lineTotal";
        public BifrostOperationKind Kind => BifrostOperationKind.Query;
        public string Signature => "(claimId: Int!): Float";

        public async ValueTask<object?> ExecuteAsync(BifrostOperationContext context)
        {
            Interlocked.Increment(ref Runs);
            var lines = await context.QueryAsync("claim_lines",
                new Dictionary<string, object?> { ["claim_id"] = context.GetArgument<int>("claimId") });
            return lines.Sum(line => Convert.ToDouble(line["units"]) * Convert.ToDouble(line["price"]));
        }
    }

    private sealed class ImportLines : IBifrostOperation
    {
        public int Runs;
        public string Name => "importLines";
        public BifrostOperationKind Kind => BifrostOperationKind.Mutation;
        public string Signature => "(claimId: Int!, csv: String!): ImportResult!";
        public string TypeDefinitions => "type ImportResult { imported: Int! claimTitle: String }";

        public async ValueTask<object?> ExecuteAsync(BifrostOperationContext context)
        {
            Interlocked.Increment(ref Runs);
            var claimId = context.GetArgument<int>("claimId");
            var claim = (await context.QueryAsync("claims", new Dictionary<string, object?> { ["id"] = claimId }))
                .SingleOrDefault() ?? throw new BifrostExecutionError("The claim is not available.");

            var imported = 0;
            foreach (var line in context.GetArgument<string>("csv")!.Split('\n'))
            {
                var cells = line.Split(',');
                await context.InsertAsync("claim_lines", new Dictionary<string, object?>
                {
                    ["claim_id"] = claimId, ["code"] = cells[0],
                    ["units"] = int.Parse(cells[1]), ["price"] = double.Parse(cells[2]),
                });
                imported++;
            }
            return new Dictionary<string, object?> { ["imported"] = imported, ["claimTitle"] = claim["title"] };
        }
    }

    private sealed class Unlisted : IBifrostOperation
    {
        public string Name => "unlisted";
        public BifrostOperationKind Kind => BifrostOperationKind.Query;
        public string Signature => ": Int";
        public ValueTask<object?> ExecuteAsync(BifrostOperationContext context) => new(1);
    }

    private sealed class NamedOperation(string name) : IBifrostOperation
    {
        public string Name => name;
        public BifrostOperationKind Kind => BifrostOperationKind.Query;
        public string Signature => ": Int";
        public ValueTask<object?> ExecuteAsync(BifrostOperationContext context) => new(1);
    }

    private sealed class RecordingObserver : IMutationObserver
    {
        public List<string> Tables { get; } = new();

        public ValueTask OnMutationAsync(MutationObserverContext context)
        {
            lock (Tables) Tables.Add(context.Table.DbName);
            return ValueTask.CompletedTask;
        }
    }

    // ---- harness ----

    private static IDictionary<string, object?> Estimator(int tenantId) => new Dictionary<string, object?>
    {
        ["tenant_id"] = tenantId, ["user_id"] = "eve", ["roles"] = new[] { "estimator" },
    };

    private static async Task<IDbModel> LoadModelAsync(IReadOnlyCollection<string> rules)
        => await new DbModelLoader(new SqliteDbConnFactory(ConnString), new MetadataLoader(rules)).LoadAsync();

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(string table, string where)
    {
        await using var cmd = new SqliteCommand($"SELECT COUNT(*) FROM {table} WHERE {where}", _keepAlive);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    // The host composition: built-in security/audit transformers on both paths, a mutation
    // observer, and the schema built with the registered operations.
    private async Task<ExecutionResult> ExecuteAsync(string document, IDictionary<string, object?> userContext)
    {
        var schema = DbSchema.FromModel(_model, null, new IBifrostOperation[] { _lineTotal, _importLines, new Unlisted() });
        var services = new ServiceCollection();
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[]
            {
                new PolicyMutationTransformer(), new TenantMutationTransformer(), new AuditMutationTransformer(),
            },
        });
        services.AddSingleton(new MutationObservers(new IMutationObserver[] { _observer }));
        await using var provider = services.BuildServiceProvider();

        return await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = document;
            options.RequestServices = provider;
            options.UserContext = userContext;
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = new SqliteDbConnFactory(ConnString),
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema, new QueryTransformerService(
                    new FilterTransformersWrap { Transformers = new IFilterTransformer[] { new TenantFilterTransformer() } })),
            });
        });
    }
}
