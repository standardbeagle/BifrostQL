using System.Collections.Concurrent;
using System.Globalization;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Operations;
using BifrostQL.Integration.Test.Infrastructure;
using FluentAssertions;

namespace BifrostQL.Integration.Test;

/// <summary>
/// <c>DeltaAsync</c> writes an operation's rows through the batch pipeline, as the caller, in the
/// operation's own transaction: a run of inserted rows is one <c>INSERT … RETURNING</c> whatever its
/// size, under neither <c>batch-max-size</c> nor <c>bulk-batch-threshold</c>, and the chain sees the
/// write's connection for every row. Statement-level triggers record each write statement the
/// database ran on <c>items</c>, under the caller that wrote it.
/// </summary>
public sealed class OperationDeltaTests : IAsyncLifetime
{
    private const string Ddl =
        """
        CREATE TABLE items (id SERIAL PRIMARY KEY, owner_id INT NOT NULL, n INT NOT NULL);
        CREATE TABLE write_statements (owner_id INT NOT NULL, kind TEXT NOT NULL);
        CREATE FUNCTION count_write() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                INSERT INTO write_statements SELECT DISTINCT owner_id, TG_OP FROM old_rows;
            ELSE
                INSERT INTO write_statements SELECT DISTINCT owner_id, TG_OP FROM new_rows;
            END IF;
            RETURN NULL;
        END $$;
        CREATE TRIGGER items_insert_statements AFTER INSERT ON items
            REFERENCING NEW TABLE AS new_rows FOR EACH STATEMENT EXECUTE FUNCTION count_write();
        CREATE TRIGGER items_update_statements AFTER UPDATE ON items
            REFERENCING NEW TABLE AS new_rows FOR EACH STATEMENT EXECUTE FUNCTION count_write();
        CREATE TRIGGER items_delete_statements AFTER DELETE ON items
            REFERENCING OLD TABLE AS old_rows FOR EACH STATEMENT EXECUTE FUNCTION count_write();
        """;

    private readonly ConcurrentQueue<(string User, bool SawConnection)> _chainRuns = new();
    private PostgresGraphQlHost? _host;

    public async Task InitializeAsync()
    {
        if (PostgresGraphQlHost.Master is null) return;
        _host = await PostgresGraphQlHost.StartAsync(
            "bifrost_operation_delta", Ddl,
            new[]
            {
                ":root { operations: insertRows[clerk], insertRowsThenThrow[clerk] }",
                "public.items.owner_id { populate: host }",
            },
            bifrost: options => options
                .AddOperation<InsertRows>()
                .AddOperation<InsertRowsThenThrow>()
                .AddMutationTransformers(new IMutationTransformer[] { new StampOwner_RefuseThirteen(_chainRuns) }));
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task Delta_WritesItsInsertedRowsInOneInsert_InTheOperationsTransaction()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        // 300 is past the default batch-max-size (100) and bulk-batch-threshold (50).
        foreach (var (user, count) in new[] { ("7", 1), ("8", 300) })
        {
            var reply = await _host!.PostAsync($$"""mutation { insertRows(count: {{count}}) }""", user, "clerk");

            PostgresGraphQlHost.AssertNoErrors(reply);
            reply.RootElement.GetProperty("data").GetProperty("insertRows").GetInt32().Should().Be(count);
            (await _host.CountAsync($"SELECT count(*) FROM items WHERE owner_id = {user}")).Should().Be(count);
            (await _host.RowsAsync($"SELECT kind FROM write_statements WHERE owner_id = {user}"))
                .Select(r => r["kind"]).Should().Equal(new[] { "INSERT" }, "{0} rows, one INSERT and no other write", count);
            _chainRuns.Where(r => r.User == user).Should().HaveCount(count).And.OnlyContain(r => r.SawConnection);
        }

        var thrown = await _host!.PostAsync("""mutation { insertRowsThenThrow }""", "10", "clerk");
        PostgresGraphQlHost.FirstError(thrown);
        (await _host.CountAsync("SELECT count(*) FROM items WHERE owner_id = 10")).Should().Be(0,
            "the rows were written in the operation's transaction, which the throw rolled back");
    }

    [SkippableFact]
    public async Task Delta_RefusesWhole_WhenATransformerRefusesOneRow()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync("""mutation { insertRows(count: 25) }""", "9", "clerk");

        PostgresGraphQlHost.FirstError(reply).GetProperty("message").GetString().Should().Contain("thirteen");
        (await _host.CountAsync("SELECT count(*) FROM items WHERE owner_id = 9")).Should().Be(0);
        (await _host.CountAsync("SELECT count(*) FROM write_statements WHERE owner_id = 9")).Should().Be(0);
    }

    private static BifrostDelta Rows(int count) => new()
    {
        Inserted = Enumerable.Range(1, count)
            .Select(n => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["n"] = n }).ToList(),
    };

    private sealed class InsertRows : IBifrostOperation
    {
        public string Name => "insertRows";
        public BifrostOperationKind Kind => BifrostOperationKind.Mutation;
        public string Signature => "(count: Int!): Int";

        public async ValueTask<object?> ExecuteAsync(BifrostOperationContext context)
            => await context.DeltaAsync("items", Rows(context.GetArgument<int>("count")));
    }

    private sealed class InsertRowsThenThrow : IBifrostOperation
    {
        public string Name => "insertRowsThenThrow";
        public BifrostOperationKind Kind => BifrostOperationKind.Mutation;
        public string Signature => ": Int";

        public async ValueTask<object?> ExecuteAsync(BifrostOperationContext context)
        {
            await context.DeltaAsync("items", Rows(25));
            throw new BifrostQL.Core.Resolvers.BifrostExecutionError("after the delta");
        }
    }

    /// <summary>
    /// Requires the write transaction; stamps the caller as owner, records whether it saw the
    /// write's connection, and refuses caller 9's row whose <c>n</c> is 13.
    /// </summary>
    private sealed class StampOwner_RefuseThirteen(ConcurrentQueue<(string, bool)> runs) : IMutationTransformer
    {
        public int Priority => 200;

        public bool RequiresWriteTransaction(IDbTable table) => table.DbName == "items";

        public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context)
            => mutationType == MutationType.Insert && table.DbName == "items";

        public ValueTask<MutationTransformResult> TransformAsync(
            IDbTable table, MutationType mutationType, Dictionary<string, object?> data, MutationTransformContext context)
        {
            var user = PolicyIdentity.FromUserContext(context.UserContext).Id;
            runs.Enqueue((user, context.Connection is not null && context.Transaction is not null));
            data["owner_id"] = int.Parse(user, CultureInfo.InvariantCulture);
            var refused = user == "9" && Convert.ToInt32(data["n"], CultureInfo.InvariantCulture) == 13;
            return ValueTask.FromResult(new MutationTransformResult
            {
                MutationType = mutationType,
                Data = data,
                Errors = refused ? new[] { "row thirteen is refused" } : Array.Empty<string>(),
            });
        }
    }
}
