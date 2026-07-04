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
/// End-to-end proof of the before-commit veto phase: a registered
/// IBeforeCommitMutationHook runs immediately before the single-statement write
/// in DbTableMutateResolver. When it returns an error the mutation aborts and no
/// row is written/changed (verified by querying the table afterward), and the
/// post-commit IMutationObserver does NOT fire. On success the write lands and
/// the post-commit observer fires exactly once.
/// </summary>
public sealed class BeforeCommitMutationHookTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_before_commit_hook_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS widgets");
        // The CHECK constraint is enforced by the database but invisible to the
        // GraphQL layer, so a write of name = 'boom' passes GraphQL validation and
        // the before-commit hook, then fails at the DB — exercising the
        // transaction's rollback path in DbTableMutateResolver.
        await Exec(
            """
            CREATE TABLE widgets (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL CHECK (name <> 'boom')
            )
            """);
        await Exec("INSERT INTO widgets(id, name) VALUES (1, 'original')");

        var factory = new SqliteDbConnFactory(ConnString);
        var loader = new DbModelLoader(factory, new MetadataLoader(Array.Empty<string>()));
        _model = await loader.LoadAsync();
    }

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(string where)
    {
        await using var cmd = new SqliteCommand($"SELECT COUNT(*) FROM widgets WHERE {where}", _keepAlive);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    [Fact]
    public async Task VetoingHook_AbortsInsert_NoRowWritten_AndPostCommitObserverDoesNotFire()
    {
        var observer = new CapturingObserver();
        var hook = new VetoHook(veto: true);

        var result = await ExecuteMutationAsync(
            "mutation { widgets(insert: { name: \"vetoed\" }) }", hook, observer);

        // The veto surfaces as a GraphQL execution error.
        result.Errors.Should().NotBeNullOrEmpty();
        result.Errors!.Single().Message.Should().Contain("nope");

        hook.Calls.Should().Be(1, "the hook ran before the write");
        // No row was written — the table still has only the seeded row.
        (await CountAsync("name = 'vetoed'")).Should().Be(0, "a veto aborts before the INSERT executes");
        (await CountAsync("1 = 1")).Should().Be(1, "only the seeded row remains");
        observer.Contexts.Should().BeEmpty("the post-commit observer must not fire when the write is vetoed");
    }

    [Fact]
    public async Task VetoingHook_AbortsUpdate_NoRowChanged()
    {
        var hook = new VetoHook(veto: true);

        var result = await ExecuteMutationAsync(
            "mutation { widgets(update: { id: 1, name: \"changed\" }) }", hook, observer: null);

        result.Errors.Should().NotBeNullOrEmpty();
        (await CountAsync("id = 1 AND name = 'original'")).Should().Be(1, "the UPDATE never executed");
        (await CountAsync("name = 'changed'")).Should().Be(0);
    }

    [Fact]
    public async Task PassingHook_AllowsInsert_AndPostCommitObserverFiresOnce()
    {
        var observer = new CapturingObserver();
        var hook = new VetoHook(veto: false);

        var result = await ExecuteMutationAsync(
            "mutation { widgets(insert: { name: \"allowed\" }) }", hook, observer);

        result.Errors.Should().BeNullOrEmpty();
        hook.Calls.Should().Be(1, "the hook ran before the write");
        (await CountAsync("name = 'allowed'")).Should().Be(1, "a passing hook lets the INSERT proceed");
        observer.Contexts.Should().ContainSingle("the post-commit observer fires once on success")
            .Which.MutationType.Should().Be(MutationType.Insert);
    }

    [Fact]
    public async Task FailedWrite_RollsBack_NoPartialData_AndPostCommitObserverDoesNotFire()
    {
        var observer = new CapturingObserver();
        var hook = new VetoHook(veto: false);

        // The hook passes, so the write is attempted inside the transaction and the
        // DB rejects it via the CHECK constraint. The transaction must roll back:
        // the table is unchanged and the post-commit observer never fires.
        var result = await ExecuteMutationAsync(
            "mutation { widgets(insert: { name: \"boom\" }) }", hook, observer);

        result.Errors.Should().NotBeNullOrEmpty("the CHECK violation surfaces as an execution error");
        hook.Calls.Should().Be(1, "the hook ran before the failed write");
        (await CountAsync("name = 'boom'")).Should().Be(0, "the failed write is rolled back");
        (await CountAsync("1 = 1")).Should().Be(1, "only the seeded row remains after rollback");
        observer.Contexts.Should().BeEmpty("the post-commit observer must not fire when the write fails");
    }

    [Fact]
    public async Task FailedWrite_RollsBack_LeavesRowUnchanged_OnUpdate()
    {
        var observer = new CapturingObserver();
        var hook = new VetoHook(veto: false);

        // CHECK violation on an UPDATE: the hook passes, the write is attempted
        // and the DB rejects name = 'boom', so the transaction rolls back and the
        // original row value survives.
        var result = await ExecuteMutationAsync(
            "mutation { widgets(update: { id: 1, name: \"boom\" }) }", hook, observer);

        result.Errors.Should().NotBeNullOrEmpty("the CHECK violation surfaces as an execution error");
        (await CountAsync("id = 1 AND name = 'original'")).Should().Be(1, "the rejected UPDATE is rolled back");
        observer.Contexts.Should().BeEmpty("the post-commit observer must not fire when the write fails");
    }

    private async Task<ExecutionResult> ExecuteMutationAsync(
        string mutation, IBeforeCommitMutationHook hook, IMutationObserver? observer)
    {
        var schema = DbSchema.FromModel(_model);
        var factory = new SqliteDbConnFactory(ConnString);

        var services = new ServiceCollection();
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap
        {
            Transformers = Array.Empty<IMutationTransformer>(),
        });
        // Register the before-commit hook composite exactly as the host DI does:
        // built from every registered IBeforeCommitMutationHook.
        services.AddSingleton<IBeforeCommitMutationHook>(hook);
        services.AddSingleton<BeforeCommitMutationHooks>(sp => new BeforeCommitMutationHooks(
            sp.GetServices<IBeforeCommitMutationHook>().ToArray()));
        if (observer != null)
        {
            services.AddSingleton<IMutationObserver>(observer);
            services.AddSingleton<MutationObservers>(sp => new MutationObservers(
                sp.GetServices<IMutationObserver>().ToArray()));
        }
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

    private sealed class VetoHook : IBeforeCommitMutationHook
    {
        private readonly bool _veto;
        public int Calls { get; private set; }

        public VetoHook(bool veto) => _veto = veto;

        public ValueTask<IReadOnlyList<string>> BeforeCommitAsync(MutationObserverContext context)
        {
            Calls++;
            // Result is not known in the before-commit phase.
            context.Result.Should().BeNull();
            IReadOnlyList<string> errors = _veto ? new[] { "nope" } : Array.Empty<string>();
            return ValueTask.FromResult(errors);
        }
    }

    private sealed class CapturingObserver : IMutationObserver
    {
        public List<MutationObserverContext> Contexts { get; } = new();
        public ValueTask OnMutationAsync(MutationObserverContext context)
        {
            Contexts.Add(context);
            return ValueTask.CompletedTask;
        }
    }
}
