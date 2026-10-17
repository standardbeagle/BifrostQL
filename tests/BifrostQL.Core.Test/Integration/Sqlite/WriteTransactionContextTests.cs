using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Server;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// Every transformer chain run on a write path receives the write's own connection and
/// transaction (<see cref="MutationTransformContext.Connection"/>), so a guard can read
/// and lock state that commits with the write. Before this, only the single-row update
/// (and batch/tree-sync) ran its chain inside the transaction; insert, delete and the
/// filtered set-update ran it before the transaction opened, and NO path exposed the
/// connection — a host guard (a reservation count, a parent lookup) had to open its own
/// connection, which can neither see the mutation's uncommitted rows nor hold a lock
/// across it.
///
/// <para>The write paths form a closed set, so there is one fact per path × verb rather
/// than a representative (regression-test-non-vacuous.md). The probing transformer does
/// not merely check for non-null members: it RUNS a command on the context's connection
/// and transaction, which Microsoft.Data.Sqlite refuses unless that transaction is the
/// connection's live one — so a fact passes only when the chain is genuinely inside the
/// write. Built on the production <c>AddBifrostQL</c> container.</para>
/// </summary>
public sealed class WriteTransactionContextTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_write_tx_context_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();
        foreach (var drop in new[] { "parts", "widgets" })
            await Exec($"DROP TABLE IF EXISTS {drop}");
        await Exec("CREATE TABLE widgets (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");
        await Exec("CREATE TABLE parts (id INTEGER PRIMARY KEY, widget_id INTEGER NOT NULL REFERENCES widgets(id), name TEXT NOT NULL)");
        await Exec("INSERT INTO widgets(id, name) VALUES (1, 'one'), (2, 'two')");
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task SingleInsert_ChainRunsInsideTheWriteTransaction()
    {
        var probe = await RunAsync("mutation { widgets(insert: { name: \"new\" }) }");

        probe.ShouldHaveRunInside(MutationType.Insert, expectTransactionObject: true);
    }

    [Fact]
    public async Task SingleUpdate_ChainRunsInsideTheWriteTransaction()
    {
        var probe = await RunAsync("mutation { widgets(update: { id: 1, name: \"renamed\" }) }");

        probe.ShouldHaveRunInside(MutationType.Update, expectTransactionObject: true);
    }

    [Fact]
    public async Task SingleDelete_ChainRunsInsideTheWriteTransaction()
    {
        var probe = await RunAsync("mutation { widgets(delete: { id: 2 }) }");

        probe.ShouldHaveRunInside(MutationType.Delete, expectTransactionObject: true);
    }

    [Fact]
    public async Task BatchInsert_ChainRunsInsideTheWriteTransaction()
    {
        var probe = await RunAsync("mutation { widgets_batch(actions: [ { insert: { name: \"a\" } } ]) }");

        probe.ShouldHaveRunInside(MutationType.Insert, expectTransactionObject: true);
    }

    [Fact]
    public async Task BatchUpdate_ChainRunsInsideTheWriteTransaction()
    {
        var probe = await RunAsync("mutation { widgets_batch(actions: [ { update: { id: 1, name: \"b\" } } ]) }");

        probe.ShouldHaveRunInside(MutationType.Update, expectTransactionObject: true);
    }

    [Fact]
    public async Task BatchDelete_ChainRunsInsideTheWriteTransaction()
    {
        var probe = await RunAsync("mutation { widgets_batch(actions: [ { delete: { id: 2 } } ]) }");

        probe.ShouldHaveRunInside(MutationType.Delete, expectTransactionObject: true);
    }

    [Fact]
    public async Task FilteredUpdate_ChainRunsInsideTheWriteTransaction()
    {
        var probe = await RunAsync(
            "mutation { widgets(updateWhere: { set: { name: \"bulk\" }, where: { name: { _eq: \"one\" } } }) }",
            filteredUpdate: true);

        probe.ShouldHaveRunInside(MutationType.Update, expectTransactionObject: true);
    }

    [Fact]
    public async Task TreeSync_ChainRunsOnTheTreeConnection_WithNoTransactionObject()
    {
        // Tree-sync manages its transaction with SQL BEGIN/COMMIT on the connection, so
        // the context carries the connection and a null Transaction — commands run on the
        // ambient transaction. The probe's command proves the connection is the tree's.
        var probe = await RunAsync("mutation { widgets(sync: { name: \"root\", parts: [ { name: \"p\" } ] }) }");

        probe.ShouldHaveRunInside(MutationType.Insert, expectTransactionObject: false);
    }

    private async Task<TransactionProbe> RunAsync(string mutation, bool filteredUpdate = false)
    {
        var probe = new TransactionProbe();
        var model = await new DbModelLoader(
            new SqliteDbConnFactory(ConnString), new MetadataLoader(Array.Empty<string>())).LoadAsync();
        if (filteredUpdate)
            model.GetTableFromDbName("widgets").Metadata[MetadataKeys.FilteredUpdate.Enabled] = FilteredUpdateConfig.EnabledValue;

        var schema = DbSchema.FromModel(model);
        await using var provider = BuildProductionServices(probe);
        var result = await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = mutation;
            options.RequestServices = provider;
            options.UserContext = new Dictionary<string, object?>();
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = new SqliteDbConnFactory(ConnString),
                ["model"] = model,
                ["tableReaderFactory"] = new SqlExecutionManager(model, schema, NullQueryTransformerService.Instance),
            });
        });
        result.Errors.Should().BeNullOrEmpty(
            $"errors: {string.Join("; ", result.Errors?.Select(e => e.Message) ?? Array.Empty<string>())}");
        return probe;
    }

    private static ServiceProvider BuildProductionServices(TransactionProbe probe)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bifrost:DisableAuth"] = "true",
                ["Bifrost:Path"] = "/graphql",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBifrostQL(o => o
            .BindConfiguration(config.GetSection("Bifrost"))
            .BindConnectionString(ConnString)
            .BindProvider("sqlite")
            .AddMutationTransformers(new IMutationTransformer[] { probe }));
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Records, per chain run, whether the context carried a connection/dialect/transaction
    /// and whether a command issued on them executed. Application-band priority, so it is
    /// an ordinary consumer module.
    /// </summary>
    private sealed class TransactionProbe : IMutationTransformer
    {
        public List<(MutationType Type, bool HasConnection, bool HasTransaction, bool HasDialect, bool CommandRan)> Runs { get; } = new();

        public int Priority => 250;

        public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context)
            => table.DbName == "widgets";

        public async ValueTask<MutationTransformResult> TransformAsync(
            IDbTable table, MutationType mutationType, Dictionary<string, object?> data, MutationTransformContext context)
        {
            var commandRan = false;
            if (context.Connection is { } conn)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = context.Transaction;
                cmd.CommandText = "SELECT COUNT(*) FROM widgets";
                await cmd.ExecuteScalarAsync(context.CancellationToken);
                commandRan = true;
            }
            Runs.Add((mutationType, context.Connection is not null, context.Transaction is not null, context.Dialect is not null, commandRan));
            return new MutationTransformResult { MutationType = mutationType, Data = data };
        }

        public void ShouldHaveRunInside(MutationType type, bool expectTransactionObject)
        {
            var runs = Runs.Where(r => r.Type == type).ToList();
            runs.Should().NotBeEmpty($"the chain must run for the {type}");
            foreach (var run in runs)
            {
                run.HasConnection.Should().BeTrue($"the {type} chain must receive the write's connection");
                run.HasDialect.Should().BeTrue($"the {type} chain must receive the write's dialect");
                run.HasTransaction.Should().Be(expectTransactionObject);
                run.CommandRan.Should().BeTrue("a command on the context's connection and transaction must execute");
            }
        }
    }
}
