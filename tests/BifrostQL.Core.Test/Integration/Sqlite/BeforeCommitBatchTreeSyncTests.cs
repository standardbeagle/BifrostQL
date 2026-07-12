using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// Proves the before-commit hook phase now covers the batch and TreeSync (nested) write
/// paths, not just single-row mutations. Before this, a hook that observed or vetoed a
/// write saw only the rows that happened to arrive one at a time — a batch or a nested
/// sync slipped past it, which for an observer (CDC, change history) means an incomplete
/// record and for a veto means a bypassed gate.
///
/// The guarantees pinned here: a hook sees EVERY batch action and EVERY tree operation,
/// before its write; and a veto aborts the whole enclosing transaction, because a batch or
/// a nested sync is one transaction — a rejected row cannot leave its siblings committed.
/// </summary>
public sealed class BeforeCommitBatchTreeSyncTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_before_commit_batch_tree_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        foreach (var drop in new[] { "posts", "blogs", "widgets" })
            await Exec($"DROP TABLE IF EXISTS {drop}");

        await Exec("CREATE TABLE widgets (id INTEGER PRIMARY KEY, name TEXT NULL)");
        await Exec("CREATE TABLE blogs (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");
        await Exec(
            """
            CREATE TABLE posts (
                id INTEGER PRIMARY KEY,
                blog_id INTEGER NOT NULL REFERENCES blogs(id),
                title TEXT NOT NULL
            )
            """);
        await Exec("INSERT INTO widgets(id, name) VALUES (1, 'original')");

        _model = await new DbModelLoader(
            new SqliteDbConnFactory(ConnString), new MetadataLoader(Array.Empty<string>())).LoadAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

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

    [Fact]
    public async Task Batch_FiresBeforeCommitHook_ForEveryAction()
    {
        var hook = new RecordingHook();

        var result = await ExecuteMutationAsync(
            "mutation { widgets_batch(actions: [ { insert: { name: \"a\" } }, { insert: { name: \"b\" } }, " +
            "{ update: { id: 1, name: \"renamed\" } }, { delete: { id: 1 } } ]) }",
            hook);

        result.Errors.Should().BeNullOrEmpty();
        hook.Seen.Should().Equal(
            MutationType.Insert, MutationType.Insert, MutationType.Update, MutationType.Delete);
        hook.SawResult.Should().BeFalse("the write has not happened yet in the before-commit phase");
    }

    [Fact]
    public async Task Batch_FiresBeforeCommitHook_ForUpsertAction()
    {
        // Arrange: the batch upsert is driven through the pipeline as an update (the
        // single-statement ON CONFLICT path), for a new key and an existing key alike.
        // The before-commit hook must fire for it like any other action — an upsert that
        // slipped past the hook would be a write no observer saw and no veto could stop.
        var hook = new RecordingHook();

        // Act
        var result = await ExecuteMutationAsync(
            "mutation { widgets_batch(actions: [ { upsert: { id: 1, name: \"upserted\" } }, " +
            "{ upsert: { id: 77, name: \"fresh\" } } ]) }",
            hook);

        // Assert
        result.Errors.Should().BeNullOrEmpty();
        hook.Seen.Should().Equal(new[] { MutationType.Update, MutationType.Update },
            "an upsert is driven through the pipeline as an update, whether it inserts or updates");
        hook.Tables.Should().Equal("widgets", "widgets");
        hook.SawResult.Should().BeFalse("the write has not happened yet in the before-commit phase");
        (await CountAsync("widgets", "id = 1 AND name = 'upserted'")).Should().Be(1, "the existing row was updated");
        (await CountAsync("widgets", "id = 77 AND name = 'fresh'")).Should().Be(1, "the new row was inserted");
    }

    [Fact]
    public async Task Batch_HookVeto_RollsBackEveryActionInTheBatch()
    {
        // The hook vetoes the SECOND action. The first action's insert already ran inside
        // the transaction — it must roll back with the batch, or a rejected row would leave
        // its siblings committed.
        var hook = new RecordingHook(vetoOnCall: 2);

        var result = await ExecuteMutationAsync(
            "mutation { widgets_batch(actions: [ { insert: { name: \"first\" } }, { insert: { name: \"second\" } } ]) }",
            hook);

        result.Errors.Should().NotBeNullOrEmpty();
        result.Errors!.Single().Message.Should().Contain("nope");
        (await CountAsync("widgets", "name = 'first'")).Should().Be(0, "the whole batch rolled back");
        (await CountAsync("widgets", "name = 'second'")).Should().Be(0, "the vetoed write never ran");
        (await CountAsync("widgets", "1 = 1")).Should().Be(1, "only the seeded row remains");
    }

    [Fact]
    public async Task TreeSync_FiresBeforeCommitHook_ForEveryOperationInTheTree()
    {
        var hook = new RecordingHook();

        var result = await ExecuteMutationAsync(
            "mutation { blogs(sync: { name: \"B\", posts: [ { title: \"first\" }, { title: \"second\" } ] }) }",
            hook);

        result.Errors.Should().BeNullOrEmpty();
        hook.Seen.Should().HaveCount(3, "the parent blog and both child posts each fire the hook");
        hook.Seen.Should().AllBeEquivalentTo(MutationType.Insert);
        hook.Tables.Should().Equal("blogs", "posts", "posts");
    }

    [Fact]
    public async Task TreeSync_HookVetoOnChild_RollsBackTheWholeTree()
    {
        // The hook vetoes the child post (call 2). The parent blog's insert already ran in
        // the same SQL-level transaction and must roll back with it — a nested sync is one
        // transaction, so a rejected child cannot leave an orphaned parent behind.
        var hook = new RecordingHook(vetoOnCall: 2);

        var result = await ExecuteMutationAsync(
            "mutation { blogs(sync: { name: \"B\", posts: [ { title: \"first\" } ] }) }",
            hook);

        result.Errors.Should().NotBeNullOrEmpty();
        (await CountAsync("blogs", "name = 'B'")).Should().Be(0, "the parent rolled back with the vetoed child");
        (await CountAsync("posts", "title = 'first'")).Should().Be(0);
    }

    private async Task<ExecutionResult> ExecuteMutationAsync(string mutation, IBeforeCommitMutationHook hook)
    {
        var schema = DbSchema.FromModel(_model);
        var factory = new SqliteDbConnFactory(ConnString);

        var services = new ServiceCollection();
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap
        {
            Transformers = Array.Empty<IMutationTransformer>(),
        });
        services.AddSingleton<IBeforeCommitMutationHook>(hook);
        services.AddSingleton<BeforeCommitMutationHooks>(sp => new BeforeCommitMutationHooks(
            sp.GetServices<IBeforeCommitMutationHook>().ToArray()));
        await using var provider = services.BuildServiceProvider();

        var executor = new DocumentExecuter();
        return await executor.ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = mutation;
            options.RequestServices = provider;
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = factory,
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema),
            });
        });
    }

    // Records what each call saw, and can veto the Nth call so a rejected row's siblings
    // can be checked for rollback.
    private sealed class RecordingHook : IBeforeCommitMutationHook
    {
        private readonly int _vetoOnCall;
        private int _calls;

        public RecordingHook(int vetoOnCall = 0) => _vetoOnCall = vetoOnCall;

        public List<MutationType> Seen { get; } = new();
        public List<string> Tables { get; } = new();
        public bool SawResult { get; private set; }

        public ValueTask<IReadOnlyList<string>> BeforeCommitAsync(MutationObserverContext context)
        {
            _calls++;
            Seen.Add(context.MutationType);
            Tables.Add(context.Table.DbName);
            if (context.Result is not null)
                SawResult = true;

            // A hook that writes must be able to: the connection (and the model/dialect it
            // needs to build that write) is supplied on every path, including TreeSync,
            // where the transaction is SQL-level and no DbTransaction object exists.
            context.Connection.Should().NotBeNull();
            context.Model.Should().NotBeNull();
            context.Dialect.Should().NotBeNull();

            IReadOnlyList<string> errors = _calls == _vetoOnCall
                ? new[] { "nope" }
                : Array.Empty<string>();
            return ValueTask.FromResult(errors);
        }
    }
}
