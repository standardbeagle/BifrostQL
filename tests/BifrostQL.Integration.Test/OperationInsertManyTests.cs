using System.Globalization;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Operations;
using BifrostQL.Integration.Test.Infrastructure;
using FluentAssertions;

namespace BifrostQL.Integration.Test;

/// <summary>
/// <c>InsertManyAsync</c> writes an operation's rows in one <c>INSERT</c>, inside the operation's
/// transaction, as the caller, and returns the keys in row order. A statement-level trigger counts
/// the INSERT statements the database actually ran on <c>items</c>.
/// </summary>
public sealed class OperationInsertManyTests : IAsyncLifetime
{
    private const string Ddl =
        """
        CREATE TABLE items (id SERIAL PRIMARY KEY, owner_id INT NOT NULL, n INT NOT NULL);
        CREATE TABLE insert_statements (n INT NOT NULL);
        INSERT INTO insert_statements VALUES (0);
        CREATE FUNCTION count_insert() RETURNS trigger LANGUAGE plpgsql AS
            $$ BEGIN UPDATE insert_statements SET n = n + 1; RETURN NULL; END $$;
        CREATE TRIGGER items_insert_statements AFTER INSERT ON items
            FOR EACH STATEMENT EXECUTE FUNCTION count_insert();
        """;

    private PostgresGraphQlHost? _host;

    public async Task InitializeAsync()
    {
        if (PostgresGraphQlHost.Master is null) return;
        _host = await PostgresGraphQlHost.StartAsync(
            "bifrost_insert_many", Ddl,
            new[] { ":root { operations: insertMany[clerk], insertManyThenThrow[clerk], insertManyRefused[clerk] }" },
            bifrost: options => options
                .AddOperation<InsertMany>()
                .AddOperation<InsertManyThenThrow>()
                .AddOperation<InsertManyRefused>()
                .AddMutationTransformers(new IMutationTransformer[] { new StampOwner_RefuseThirteen() }));
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task InsertMany_WritesItsRowsInOneStatement_InTheOperationsTransaction()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync("""mutation { insertMany }""", "7", "clerk");

        PostgresGraphQlHost.AssertNoErrors(reply);
        var keys = reply.RootElement.GetProperty("data").GetProperty("insertMany").GetString()!.Split(',');
        keys.Should().HaveCount(25);
        for (var i = 0; i < keys.Length; i++)
            (await _host.CountAsync($"SELECT n FROM items WHERE id = {int.Parse(keys[i], CultureInfo.InvariantCulture)} AND owner_id = 7"))
                .Should().Be(i + 1, "key {0} belongs to row {1}", keys[i], i);
        (await _host.CountAsync("SELECT n FROM insert_statements")).Should().Be(1, "25 rows, one INSERT");
    }

    [SkippableFact]
    public async Task InsertMany_RollsBack_WhenTheOperationThrowsAfterTheCall()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync("""mutation { insertManyThenThrow }""", "8", "clerk");

        PostgresGraphQlHost.FirstError(reply);
        (await _host.CountAsync("SELECT count(*) FROM items WHERE owner_id = 8")).Should().Be(0,
            "the insert ran in the operation's transaction, which the throw rolled back");
    }

    [SkippableFact]
    public async Task InsertMany_RefusesWhole_WhenATransformerRefusesOneRow()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync("""mutation { insertManyRefused }""", "9", "clerk");

        PostgresGraphQlHost.FirstError(reply).GetProperty("message").GetString().Should().Contain("thirteen");
        (await _host.CountAsync("SELECT count(*) FROM items WHERE owner_id = 9")).Should().Be(0);
    }

    private static IEnumerable<IReadOnlyDictionary<string, object?>> Rows(int count) =>
        Enumerable.Range(1, count).Select(n => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["n"] = n });

    private sealed class InsertMany : IBifrostOperation
    {
        public string Name => "insertMany";
        public BifrostOperationKind Kind => BifrostOperationKind.Mutation;
        public string Signature => ": String";

        public async ValueTask<object?> ExecuteAsync(BifrostOperationContext context)
        {
            var keys = await context.InsertManyAsync("items", Rows(25));
            return string.Join(',', keys.Select(k => Convert.ToString(k, CultureInfo.InvariantCulture)));
        }
    }

    private sealed class InsertManyThenThrow : IBifrostOperation
    {
        public string Name => "insertManyThenThrow";
        public BifrostOperationKind Kind => BifrostOperationKind.Mutation;
        public string Signature => ": Int";

        public async ValueTask<object?> ExecuteAsync(BifrostOperationContext context)
        {
            await context.InsertManyAsync("items", Rows(25));
            throw new BifrostQL.Core.Resolvers.BifrostExecutionError("after the insert");
        }
    }

    private sealed class InsertManyRefused : IBifrostOperation
    {
        public string Name => "insertManyRefused";
        public BifrostOperationKind Kind => BifrostOperationKind.Mutation;
        public string Signature => ": Int";

        public async ValueTask<object?> ExecuteAsync(BifrostOperationContext context)
            => (await context.InsertManyAsync("items", Rows(25))).Count;
    }

    /// <summary>Stamps the caller as owner, and refuses the row whose <c>n</c> is 13.</summary>
    private sealed class StampOwner_RefuseThirteen : IMutationTransformer
    {
        public int Priority => 200;

        public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context)
            => mutationType == MutationType.Insert && table.DbName == "items";

        public ValueTask<MutationTransformResult> TransformAsync(
            IDbTable table, MutationType mutationType, Dictionary<string, object?> data, MutationTransformContext context)
        {
            data["owner_id"] = int.Parse(BifrostQL.Core.Auth.PolicyIdentity.FromUserContext(context.UserContext).Id, CultureInfo.InvariantCulture);
            var refused = Convert.ToInt32(data["n"], CultureInfo.InvariantCulture) == 13 && (int)data["owner_id"]! == 9;
            return ValueTask.FromResult(new MutationTransformResult
            {
                MutationType = mutationType,
                Data = data,
                Errors = refused ? new[] { "row thirteen is refused" } : Array.Empty<string>(),
            });
        }
    }
}
