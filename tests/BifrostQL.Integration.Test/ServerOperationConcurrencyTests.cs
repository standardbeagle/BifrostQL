using System.Data.Common;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Deferred;
using BifrostQL.Core.Operations;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Integration.Test;

/// <summary>
/// <see cref="BifrostOperationContext.QueryAsync"/>'s sort and null tests on every dialect,
/// through the same transformers a generated read runs: what an operation may read must not
/// change because it read through the context.
/// </summary>
public abstract class ServerOperationQueryTestBase : IAsyncLifetime
{
    private const string Member = "member";

    private string? _dbName;
    private readonly DelegateOperation _queryProbe = new("queryProbe", BifrostOperationKind.Query);
    private readonly DelegateOperation _mutationProbe = new("mutationProbe", BifrostOperationKind.Mutation);
    private readonly StartTimer _startTimer = new();
    private readonly CopyWeek _copyWeek = new();

    protected IDbConnFactory ConnFactory { get; private set; } = null!;
    protected IDbModel Model { get; private set; } = null!;

    protected abstract bool Available { get; }
    protected abstract string TableSchema { get; }
    protected abstract string CreateTablesDdl { get; }
    protected abstract Task CreateDatabaseAsync(string dbName);
    protected abstract Task DropDatabaseAsync(string dbName);
    protected abstract IDbConnFactory CreateConnFactory(string dbName);

    protected DelegateOperation MutationProbe => _mutationProbe;
    protected RecordingMutationObserver MutationsObserved { get; } = new();
    protected DelegateOperation QueryProbe => _queryProbe;
    protected StartTimer StartTimerOperation => _startTimer;

    public async Task InitializeAsync()
    {
        if (!Available) return;
        _dbName = $"bifrost_op_{Guid.NewGuid():N}";
        await CreateDatabaseAsync(_dbName);
        ConnFactory = CreateConnFactory(_dbName);
        foreach (var statement in CreateTablesDdl.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            await ExecAsync(statement);

        await ExecAsync("INSERT INTO people (id, tenant_id, name) VALUES (1, 1, 'one'), (2, 1, 'two'), (3, 2, 'other tenant')");
        await ExecAsync(
            """
            INSERT INTO time_entries (tenant_id, person_id, week, note, secret) VALUES
                (1, 2, 10, 'a', 's'),
                (1, 2, 11, NULL, 's'),
                (1, 2, 12, 'c', NULL),
                (1, 2, 12, 'd', NULL),
                (2, 3, 10, NULL, NULL),
                (2, 3, 11, 'other', NULL)
            """);

        await LoadModelAsync();
    }

    protected async Task LoadModelAsync(params string[] extraMetadata)
    {
        Model = await new DbModelLoader(ConnFactory, new MetadataLoader(new[]
        {
            ":root { operations: queryProbe[member], mutationProbe[member], startTimer[member], copyWeek[member] }",
            // A member may not read or write people through the policy engine at all.
            $"{TableSchema}.people {{ tenant-filter: tenant_id }}",
            $"{TableSchema}.people {{ policy-actions: read[admin] }}",
            $"{TableSchema}.time_entries {{ tenant-filter: tenant_id }}",
            $"{TableSchema}.time_entries {{ policy-actions: read,create,update,delete }}",
            $"{TableSchema}.time_entries {{ policy-read-deny: secret }}",
            $"{TableSchema}.week_locks {{ policy-actions: read,update }}",
            $"{TableSchema}.keyless {{ policy-actions: read }}",
        }.Concat(extraMetadata).ToArray())).LoadAsync();
    }

    public async Task DisposeAsync()
    {
        if (_dbName is null) return;
        await DropDatabaseAsync(_dbName);
    }

    [SkippableFact]
    public async Task QueryAsync_Sorts_ThenLimits()
    {
        Skip.IfNot(Available, "test database environment variable not set");

        var newest = await ReadAsync(context => context.QueryAsync("time_entries",
            new Dictionary<string, object?> { ["person_id"] = 2 },
            columns: new[] { "week", "note" },
            limit: 2,
            sort: new[] { "week_desc", "note_desc" }));

        newest.Select(row => row["note"]).Should().Equal("d", "c");
    }

    [SkippableFact]
    public async Task QueryAsync_Sort_RefusesAnUnknownColumnOrAMissingDirection()
    {
        Skip.IfNot(Available, "test database environment variable not set");

        var unknown = await RunReadExpectingErrorAsync(context => context.QueryAsync("time_entries", sort: new[] { "nope_asc" }));
        var noDirection = await RunReadExpectingErrorAsync(context => context.QueryAsync("time_entries", sort: new[] { "week" }));

        unknown.Message.Should().Contain("has no column 'nope'");
        noDirection.Message.Should().Contain("'week'").And.Contain("_asc");
    }

    [SkippableFact]
    public async Task QueryAsync_WithNoLimit_ReadsTheDefaultWindow_AndMinusOneReadsPastIt()
    {
        Skip.IfNot(Available, "test database environment variable not set");
        var values = string.Join(", ", Enumerable.Range(0, 120).Select(i => $"(1, 1, {1000 + i})"));
        await ExecAsync($"INSERT INTO time_entries (tenant_id, person_id, week) VALUES {values}");
        var ofPersonOne = new Dictionary<string, object?> { ["person_id"] = 1 };

        var byDefault = await ReadAsync(context => context.QueryAsync("time_entries", ofPersonOne, new[] { "week" }));
        var all = await ReadAsync(context => context.QueryAsync("time_entries", ofPersonOne, new[] { "week" }, limit: -1));

        byDefault.Should().HaveCount(100);
        all.Should().HaveCount(120);
    }

    [SkippableFact]
    public async Task QueryAsync_NullValueMeansIsNull()
    {
        Skip.IfNot(Available, "test database environment variable not set");

        var rows = await ReadAsync(context => context.QueryAsync("time_entries",
            new Dictionary<string, object?> { ["note"] = null },
            columns: new[] { "week" }));

        rows.Select(row => Convert.ToInt32(row["week"])).Should().Equal(new[] { 11 },
            "the other tenant's row with a null note stays out of reach");
    }

    [SkippableFact]
    public async Task QueryAsync_NotNullMeansIsNotNull()
    {
        Skip.IfNot(Available, "test database environment variable not set");

        var rows = await ReadAsync(context => context.QueryAsync("time_entries",
            new Dictionary<string, object?> { ["note"] = BifrostOperationContext.NotNull },
            columns: new[] { "note" },
            sort: new[] { "note_asc" }));

        rows.Select(row => row["note"]).Should().Equal(new object?[] { "a", "c", "d" },
            "the other tenant's row with a note stays out of reach");
    }

    [SkippableFact]
    public async Task QueryAsync_LessThan_KeepsOnlySmallerValues()
    {
        Skip.IfNot(Available, "test database environment variable not set");

        var previous = await ReadAsync(context => context.QueryAsync("time_entries",
            new Dictionary<string, object?> { ["person_id"] = 2, ["week"] = BifrostOperationContext.LessThan(12) },
            columns: new[] { "week" },
            limit: 1,
            sort: new[] { "week_desc" }));

        previous.Select(row => Convert.ToInt32(row["week"])).Should().Equal(11);
    }

    [SkippableFact]
    public async Task QueryAsync_LessThan_LeavesOutNulls_AndOtherTenants()
    {
        Skip.IfNot(Available, "test database environment variable not set");

        var rows = await ReadAsync(context => context.QueryAsync("time_entries",
            new Dictionary<string, object?> { ["note"] = BifrostOperationContext.LessThan("z") },
            columns: new[] { "note" },
            sort: new[] { "note_asc" }));

        rows.Select(row => row["note"]).Should().Equal(new object?[] { "a", "c", "d" },
            "a null note compares to nothing, and the other tenant's note stays out of reach");
    }

    [SkippableFact]
    public async Task QueryAsync_LessThan_RefusesNull_BecauseNothingIsLessThanNull()
    {
        Skip.IfNot(Available, "test database environment variable not set");

        var refused = await RunReadExpectingErrorAsync(context => context.QueryAsync("time_entries",
            new Dictionary<string, object?> { ["week"] = BifrostOperationContext.LessThan(null!) }));

        refused.Code.Should().Be(BifrostExecutionError.InvalidValueCode);
    }

    [SkippableFact]
    public async Task QueryAsync_NullTestOrSort_OnAReadDeniedColumn_IsRefusedLikeAGeneratedRead()
    {
        Skip.IfNot(Available, "test database environment variable not set");

        var filtered = await RunReadExpectingErrorAsync(context => context.QueryAsync("time_entries",
            new Dictionary<string, object?> { ["secret"] = BifrostOperationContext.NotNull }, columns: new[] { "week" }));
        var nullTested = await RunReadExpectingErrorAsync(context => context.QueryAsync("time_entries",
            new Dictionary<string, object?> { ["secret"] = null }, columns: new[] { "week" }));
        var sorted = await RunReadExpectingErrorAsync(context => context.QueryAsync("time_entries",
            columns: new[] { "week" }, sort: new[] { "secret_desc" }));
        var compared = await RunReadExpectingErrorAsync(context => context.QueryAsync("time_entries",
            new Dictionary<string, object?> { ["secret"] = BifrostOperationContext.LessThan("t") }, columns: new[] { "week" }));

        foreach (var error in new[] { filtered, nullTested, sorted, compared })
            error.Code.Should().Be(BifrostExecutionError.AccessDeniedCode, "testing or ordering by a column reveals it");
    }

    // ---- harness ----

    protected async Task ExecAsync(string sql)
    {
        await using var conn = ConnFactory.GetConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    protected async Task<long> CountAsync(string where)
    {
        await using var conn = ConnFactory.GetConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM time_entries WHERE {where}";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    protected static IDictionary<string, object?> Caller(int tenantId = 1) => new Dictionary<string, object?>
    {
        ["tenant_id"] = tenantId, ["user_id"] = "mem", ["roles"] = new[] { Member },
    };

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadAsync(
        Func<BifrostOperationContext, Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>>> read)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = Array.Empty<IReadOnlyDictionary<string, object?>>();
        _queryProbe.Body = async context => rows = await read(context);
        var result = await ExecuteAsync("{ queryProbe }", Caller());
        result.Errors.Should().BeNullOrEmpty(string.Join("; ", result.Errors?.Select(e => e.Message) ?? Array.Empty<string>()));
        return rows;
    }

    private async Task<ExecutionError> RunReadExpectingErrorAsync(
        Func<BifrostOperationContext, Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>>> read)
    {
        _queryProbe.Body = context => read(context);
        var result = await ExecuteAsync("{ queryProbe }", Caller());
        return result.Errors.Should().ContainSingle().Subject;
    }

    /// <summary>
    /// The filter and mutation transformer chains a generated field runs, and no validation
    /// transformer, so a value the real host's validator refuses reaches the database here.
    /// PostgresUuidKeyTests drives the same operations through the real host.
    /// </summary>
    protected async Task<ExecutionResult> ExecuteAsync(string document, IDictionary<string, object?> userContext)
    {
        var schema = DbSchema.FromModel(Model, null, new IBifrostOperation[] { _queryProbe, _mutationProbe, _startTimer, _copyWeek });
        var filterTransformers = new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[] { new TenantFilterTransformer(), new PolicyFilterTransformer() },
        };
        var services = new ServiceCollection();
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[] { new PolicyMutationTransformer(), new TenantMutationTransformer() },
        });
        services.AddSingleton<IFilterTransformers>(filterTransformers);
        services.AddSingleton(new MutationObservers(new IMutationObserver[] { MutationsObserved }));
        await using var provider = services.BuildServiceProvider();

        return await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = document;
            options.RequestServices = provider;
            options.UserContext = userContext;
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = ConnFactory,
                ["model"] = Model,
                ["tableReaderFactory"] = new SqlExecutionManager(Model, schema, new QueryTransformerService(filterTransformers)),
            });
        });
    }

    /// <summary>Records what the post-commit notification told observers.</summary>
    protected sealed class RecordingMutationObserver : IMutationObserver
    {
        public List<string> Tables { get; } = new();

        public ValueTask OnMutationAsync(MutationObserverContext context)
        {
            lock (Tables)
                Tables.Add(context.Table.DbName);
            return ValueTask.CompletedTask;
        }
    }

    protected sealed class DelegateOperation(string name, BifrostOperationKind kind) : IBifrostOperation
    {
        public Func<BifrostOperationContext, Task> Body { get; set; } = _ => Task.CompletedTask;
        public string Name => name;
        public BifrostOperationKind Kind => kind;
        public string Signature => ": Int";

        public async ValueTask<object?> ExecuteAsync(BifrostOperationContext context)
        {
            await Body(context);
            return 0;
        }
    }

    /// <summary>
    /// Start a timer: stop the person's running entry and open a new one. A read that gates a
    /// write, which is exactly what two concurrent runs get wrong without the lock.
    /// </summary>
    protected sealed class StartTimer : IBifrostOperation
    {
        public bool TakesLock { get; set; } = true;
        public Func<Task> AfterRead { get; set; } = () => Task.CompletedTask;
        public string Name => "startTimer";
        public BifrostOperationKind Kind => BifrostOperationKind.Mutation;
        public string Signature => "(personId: Int!): Int";

        public async ValueTask<object?> ExecuteAsync(BifrostOperationContext context)
        {
            var personId = context.GetArgument<int>("personId");
            if (TakesLock)
                await context.LockRowAsync("people", new Dictionary<string, object?> { ["id"] = personId });

            var running = await context.QueryAsync("time_entries",
                new Dictionary<string, object?> { ["person_id"] = personId, ["stopped_at"] = null },
                columns: new[] { "id" });
            await AfterRead();

            foreach (var entry in running)
                await context.UpdateAsync("time_entries", new Dictionary<string, object?>
                {
                    ["id"] = entry["id"], ["stopped_at"] = DateTime.UtcNow,
                });
            await context.InsertAsync("time_entries", new Dictionary<string, object?>
            {
                ["person_id"] = personId, ["week"] = 1,
            });
            return running.Count;
        }
    }

    /// <summary>Copy a week into an empty one. The emptiness test gates the copy.</summary>
    private sealed class CopyWeek : IBifrostOperation
    {
        public string Name => "copyWeek";
        public BifrostOperationKind Kind => BifrostOperationKind.Mutation;
        public string Signature => "(personId: Int!, from: Int!, to: Int!): Int";

        public async ValueTask<object?> ExecuteAsync(BifrostOperationContext context)
        {
            var personId = context.GetArgument<int>("personId");
            await context.LockRowAsync("people", new Dictionary<string, object?> { ["id"] = personId });

            var target = await context.QueryAsync("time_entries",
                new Dictionary<string, object?> { ["person_id"] = personId, ["week"] = context.GetArgument<int>("to") },
                columns: new[] { "id" }, limit: 1);
            if (target.Count > 0)
                return 0;

            var source = await context.QueryAsync("time_entries",
                new Dictionary<string, object?> { ["person_id"] = personId, ["week"] = context.GetArgument<int>("from") },
                columns: new[] { "note" }, sort: new[] { "id_asc" });
            foreach (var entry in source)
                await context.InsertAsync("time_entries", new Dictionary<string, object?>
                {
                    ["person_id"] = personId, ["week"] = context.GetArgument<int>("to"), ["note"] = entry["note"],
                });
            return source.Count;
        }
    }
}

/// <summary>
/// <see cref="BifrostOperationContext.LockRowAsync"/> on the dialects that can hold a row lock:
/// concurrent operations serialize through it, whoever the caller is.
/// </summary>
public abstract class ServerOperationRowLockTestBase : ServerOperationQueryTestBase
{
    private const int ConcurrentRuns = 20;

    /// <summary>Runs inside a transaction: throws when another transaction holds the row.</summary>
    protected abstract string TryLockPersonOneSql { get; }

    /// <summary>
    /// Whether a locking read leaves alone a row its key found but its tenant test refused.
    /// InnoDB at repeatable read does not: it keeps the lock on every row the index scan reached.
    /// </summary>
    protected virtual bool ScopedLockSkipsOtherRows => true;

    [SkippableFact]
    public async Task ConcurrentStarts_EachStopThePreviousEntry_LeavingOneRunning()
    {
        Skip.IfNot(Available, "test database environment variable not set");

        var results = await Task.WhenAll(Enumerable.Range(0, ConcurrentRuns)
            .Select(_ => Task.Run(() => ExecuteAsync("mutation { startTimer(personId: 1) }", Caller()))));

        results.SelectMany(result => result.Errors ?? new ExecutionErrors()).Select(error => $"{error.Code}: {error.Message}")
            .Should().BeEmpty("every start serializes behind the lock rather than racing into the database");
        (await CountAsync("person_id = 1 AND stopped_at IS NULL")).Should().Be(1, "one timer is left running");
        (await CountAsync("person_id = 1 AND stopped_at IS NOT NULL")).Should().Be(ConcurrentRuns - 1, "each start stopped the one before it");
    }

    [SkippableFact]
    public async Task ConcurrentCopies_DoNotReenterTheEmptinessCheck()
    {
        Skip.IfNot(Available, "test database environment variable not set");

        var results = await Task.WhenAll(Enumerable.Range(0, ConcurrentRuns)
            .Select(_ => Task.Run(() => ExecuteAsync("mutation { copyWeek(personId: 2, from: 12, to: 40) }", Caller()))));

        results.SelectMany(result => result.Errors ?? new ExecutionErrors()).Select(error => error.Message).Should().BeEmpty();
        (await CountAsync("person_id = 2 AND week = 40")).Should().Be(2, "the two source rows were copied exactly once");
        results.Select(CopiedRows).Should().ContainSingle(copied => copied == 2)
            .And.Subject.Count().Should().Be(ConcurrentRuns);
    }

    [SkippableFact]
    public async Task LockRow_HoldsTheRowUntilTheOperationCommits()
    {
        Skip.IfNot(Available, "test database environment variable not set");
        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MutationProbe.Body = async context =>
        {
            await context.LockRowAsync("people", new Dictionary<string, object?> { ["id"] = 1 });
            locked.SetResult();
            await release.Task;
        };

        var run = ExecuteAsync("mutation { mutationProbe }", Caller());
        await Task.WhenAny(locked.Task, run);
        locked.Task.IsCompleted.Should().BeTrue("the operation should have reached its lock");

        (await TryLockPersonOneAsync()).Should().BeFalse("the operation's transaction holds the row");
        release.SetResult();
        (await run).Errors.Should().BeNullOrEmpty();
        (await TryLockPersonOneAsync()).Should().BeTrue("commit released the lock");
    }

    [SkippableFact]
    public async Task LockRow_IsReleasedWhenTheOperationRollsBack()
    {
        Skip.IfNot(Available, "test database environment variable not set");
        var locked = false;
        MutationProbe.Body = async context =>
        {
            await context.LockRowAsync("people", new Dictionary<string, object?> { ["id"] = 1 });
            locked = true;
            throw new BifrostExecutionError("changed my mind");
        };

        (await ExecuteAsync("mutation { mutationProbe }", Caller())).Errors.Should().ContainSingle();

        locked.Should().BeTrue("the operation should have taken the lock before it failed");
        (await TryLockPersonOneAsync()).Should().BeTrue();
    }

    [SkippableFact]
    public async Task LockRow_ReachesARowTheCallerMayNotWriteOrRead()
    {
        Skip.IfNot(Available, "test database environment variable not set");
        var lockedOwnRow = false;
        MutationProbe.Body = async context =>
        {
            await context.LockRowAsync("people", new Dictionary<string, object?> { ["id"] = 1 });
            lockedOwnRow = true;
        };

        (await ExecuteAsync("mutation { mutationProbe }", Caller())).Errors.Should().BeNullOrEmpty();
        lockedOwnRow.Should().BeTrue();

        MutationProbe.Body = context => context.UpdateAsync("people", new Dictionary<string, object?> { ["id"] = 1, ["name"] = "changed" });
        var write = await ExecuteAsync("mutation { mutationProbe }", Caller());
        write.Errors.Should().ContainSingle().Which.Code.Should().Be(BifrostExecutionError.AccessDeniedCode,
            "the lock is the operation's concurrency control, not a grant to write the row");

        MutationProbe.Body = context => context.QueryAsync("people", columns: new[] { "name" });
        var read = await ExecuteAsync("mutation { mutationProbe }", Caller());
        read.Errors.Should().ContainSingle().Which.Code.Should().Be(BifrostExecutionError.AccessDeniedCode,
            "nor a grant to read it");
    }

    [SkippableFact]
    public async Task LockRow_TreatsARowOutsideTheCallersTenantAsMissing()
    {
        Skip.IfNot(Available, "test database environment variable not set");
        MutationProbe.Body = context => context.LockRowAsync("people", new Dictionary<string, object?> { ["id"] = 3 });
        var outsideTenant = await ExecuteAsync("mutation { mutationProbe }", Caller(tenantId: 1));

        MutationProbe.Body = context => context.LockRowAsync("people", new Dictionary<string, object?> { ["id"] = 999 });
        var missing = await ExecuteAsync("mutation { mutationProbe }", Caller(tenantId: 1));

        var error = outsideTenant.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(BifrostOperationContext.LockRowNotFoundCode);
        var missingError = missing.Errors.Should().ContainSingle().Subject;
        error.Message.Should().Be(missingError.Message, "the caller must not learn another tenant's row exists");
    }

    [SkippableFact]
    public async Task LockRow_OutsideTheCallersTenant_TakesNoLock()
    {
        Skip.IfNot(Available, "test database environment variable not set");
        var free = false;
        MutationProbe.Body = async context =>
        {
            await FluentActions.Awaiting(() => context.LockRowAsync("people", new Dictionary<string, object?> { ["id"] = 3 }))
                .Should().ThrowAsync<BifrostExecutionError>();
            free = await TryLockPersonAsync(3);
        };

        (await ExecuteAsync("mutation { mutationProbe }", Caller(tenantId: 1))).Errors.Should().BeNullOrEmpty();

        free.Should().Be(ScopedLockSkipsOtherRows, "a tenant must not be able to block another tenant's writes");
    }

    [SkippableFact]
    public async Task LockRow_WithNoTenantInTheUserContext_IsDenied()
    {
        Skip.IfNot(Available, "test database environment variable not set");
        await LoadModelAsync($":root {{ {MetadataKeys.Operations.Tenant}: {MetadataKeys.Operations.TenantOptional} }}");
        MutationProbe.Body = context => context.LockRowAsync("people", new Dictionary<string, object?> { ["id"] = 1 });

        var noTenant = new Dictionary<string, object?> { ["user_id"] = "mem", ["roles"] = new[] { "member" } };
        var result = await ExecuteAsync("mutation { mutationProbe }", noTenant);

        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(BifrostExecutionError.AccessDeniedCode);
        error.Message.Should().Contain("Tenant context required", "the lock itself must fail closed when the operation allows no tenant");
    }

    [SkippableFact]
    public async Task ACommittedWrite_FiresTheObserver()
    {
        Skip.IfNot(Available, "test database environment variable not set");
        MutationProbe.Body = context => context.InsertAsync("time_entries", new Dictionary<string, object?> { ["person_id"] = 1, ["week"] = 61 });

        (await ExecuteAsync("mutation { mutationProbe }", Caller())).Errors.Should().BeNullOrEmpty();

        (await CountAsync("week = 61")).Should().Be(1);
        MutationsObserved.Tables.Should().ContainSingle();
    }

    [SkippableFact]
    public async Task AReadFailureCaughtAfterAWrite_RollsTheOperationBack_AndTheClientSeesIt()
    {
        Skip.IfNot(Available, "test database environment variable not set");
        await ExecAsync("DROP TABLE keyless");
        string? caughtCode = null;
        MutationProbe.Body = async context =>
        {
            await context.InsertAsync("time_entries", new Dictionary<string, object?> { ["person_id"] = 1, ["week"] = 62 });
            try
            {
                await context.QueryAsync("keyless", new Dictionary<string, object?>(), new[] { "label" });
            }
            catch (BifrostExecutionError error)
            {
                caughtCode = error.ErrorCode;
            }
        };

        var result = await ExecuteAsync("mutation { mutationProbe }", Caller());

        caughtCode.Should().Be("DATABASE_ERROR");
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("DATABASE_ERROR",
            "the client gets the first failure's code, not a report of success over a rolled-back write");
        (await CountAsync("week = 62")).Should().Be(0);
        MutationsObserved.Tables.Should().BeEmpty("no observer sees a write that was rolled back");
    }

    [SkippableFact]
    public async Task AfterACaughtReadFailure_NoStatementRuns_OnAnyEngine()
    {
        Skip.IfNot(Available, "test database environment variable not set");
        await ExecAsync("DROP TABLE keyless");
        Exception? readRefusal = null, writeRefusal = null, lockRefusal = null;
        MutationProbe.Body = async context =>
        {
            await FluentActions.Awaiting(() => context.QueryAsync("keyless", new Dictionary<string, object?>(), new[] { "label" }))
                .Should().ThrowAsync<BifrostExecutionError>();
            readRefusal = await CaptureAsync(() => context.QueryAsync("time_entries", new Dictionary<string, object?>(), new[] { "id" }));
            writeRefusal = await CaptureAsync(() => context.InsertAsync("time_entries", new Dictionary<string, object?> { ["person_id"] = 1, ["week"] = 63 }));
            lockRefusal = await CaptureAsync(() => context.LockRowAsync("people", new Dictionary<string, object?> { ["id"] = 1 }));
        };

        var result = await ExecuteAsync("mutation { mutationProbe }", Caller());

        foreach (var refusal in new[] { readRefusal, writeRefusal, lockRefusal })
            refusal.Should().BeOfType<BifrostExecutionError>().Which.ErrorCode.Should().Be("DATABASE_ERROR",
                "every engine refuses the same way, whether or not it has already aborted the transaction");
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("DATABASE_ERROR");
        (await CountAsync("week = 63")).Should().Be(0);
    }

    [SkippableFact]
    public async Task ACaughtRefusalThatRanNoStatement_LeavesTheOperationAbleToContinue()
    {
        Skip.IfNot(Available, "test database environment variable not set");
        MutationProbe.Body = async context =>
        {
            await FluentActions.Awaiting(() => context.QueryAsync("time_entries", new Dictionary<string, object?>(), new[] { "secret" }))
                .Should().ThrowAsync<BifrostExecutionError>();
            await context.InsertAsync("time_entries", new Dictionary<string, object?> { ["person_id"] = 1, ["week"] = 64 });
        };

        (await ExecuteAsync("mutation { mutationProbe }", Caller())).Errors.Should().BeNullOrEmpty();

        (await CountAsync("week = 64")).Should().Be(1, "a read-denied column is a rule, not a failed statement");
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> statement)
    {
        try
        {
            await statement();
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    [SkippableFact]
    public async Task LockRow_OnAMissingRow_SaysSo_AndLeavesTheOperationAbleToContinue()
    {
        Skip.IfNot(Available, "test database environment variable not set");

        MutationProbe.Body = context => context.LockRowAsync("people", new Dictionary<string, object?> { ["id"] = 999 });
        var uncaught = await ExecuteAsync("mutation { mutationProbe }", Caller());
        var error = uncaught.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(BifrostOperationContext.LockRowNotFoundCode);
        error.Message.Should().NotContain("999", "the key is the operation's business, not the client's");

        MutationProbe.Body = async context =>
        {
            var caught = await FluentActions.Awaiting(() => context.LockRowAsync("people", new Dictionary<string, object?> { ["id"] = 999 }))
                .Should().ThrowAsync<BifrostExecutionError>();
            await context.InsertAsync("time_entries", new Dictionary<string, object?> { ["person_id"] = 1, ["week"] = 77 });
        };
        (await ExecuteAsync("mutation { mutationProbe }", Caller())).Errors.Should().BeNullOrEmpty();
        (await CountAsync("week = 77")).Should().Be(1, "no row to lock is an answer, not a failed statement");
    }

    [SkippableFact]
    public async Task LockRow_LocksACompositeKey_AndRefusesAnythingElse()
    {
        Skip.IfNot(Available, "test database environment variable not set");
        await ExecAsync("INSERT INTO week_locks (person_id, week) VALUES (1, 10), (1, 11)");

        async Task<ExecutionResult> LockAsync(string table, Dictionary<string, object?> key)
        {
            MutationProbe.Body = context => context.LockRowAsync(table, key);
            return await ExecuteAsync("mutation { mutationProbe }", Caller());
        }

        var whole = await LockAsync("week_locks", new() { ["week"] = 10, ["person_id"] = 1 });
        var part = await LockAsync("week_locks", new() { ["person_id"] = 1 });
        var nonKey = await LockAsync("people", new() { ["name"] = "one" });
        var extra = await LockAsync("people", new() { ["id"] = 1, ["name"] = "one" });
        var noKey = await LockAsync("keyless", new() { ["label"] = "x" });
        var noColumn = await LockAsync("people", new() { ["nope"] = 1 });

        whole.Errors.Should().BeNullOrEmpty();
        foreach (var refused in new[] { part, nonKey, extra, noKey })
            refused.Errors.Should().ContainSingle().Which.Code.Should().Be(MutationArgumentBinder.PartialPrimaryKeyCode,
                "a lock addresses exactly one row, which only the whole primary key does");
        noColumn.Errors.Should().ContainSingle().Which.Message.Should().Contain("has no column 'nope'");
    }

    [SkippableFact]
    public async Task LockRow_FromAQueryOperation_IsRefused()
    {
        Skip.IfNot(Available, "test database environment variable not set");
        QueryProbe.Body = context => context.LockRowAsync("people", new Dictionary<string, object?> { ["id"] = 1 });

        var result = await ExecuteAsync("{ queryProbe }", Caller());

        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("query operation",
            "a query operation has no transaction to hold the lock in");
    }

    private static int CopiedRows(ExecutionResult result)
    {
        using var json = JsonDocument.Parse(new GraphQL.SystemTextJson.GraphQLSerializer().Serialize(result));
        return json.RootElement.GetProperty("data").GetProperty("copyWeek").GetInt32();
    }

    private Task<bool> TryLockPersonOneAsync() => TryLockPersonAsync(1);

    private async Task<bool> TryLockPersonAsync(int id)
    {
        await using var conn = ConnFactory.GetConnection();
        await conn.OpenAsync();
        await using var transaction = await conn.BeginTransactionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = TryLockPersonOneSql.Replace("id = 1", $"id = {id}");
        try
        {
            // Scalar reads the batch through: SQL Server raises a lock timeout on the read, not the execute.
            await cmd.ExecuteScalarAsync();
            return true;
        }
        catch (DbException)
        {
            return false;
        }
    }
}

public sealed class SqliteServerOperationQueryTests : ServerOperationQueryTestBase
{
    private SqliteConnection? _keepAlive;

    protected override bool Available => true;
    protected override string TableSchema => "main";

    protected override string CreateTablesDdl => """
        CREATE TABLE people (id INTEGER PRIMARY KEY, tenant_id INTEGER NOT NULL, name TEXT NOT NULL);
        CREATE TABLE time_entries (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            tenant_id INTEGER NOT NULL,
            person_id INTEGER NOT NULL,
            week INTEGER NOT NULL,
            stopped_at TEXT,
            date DATE,
            note TEXT,
            secret TEXT
        );
        CREATE TABLE week_locks (person_id INTEGER NOT NULL, week INTEGER NOT NULL, PRIMARY KEY (person_id, week));
        CREATE TABLE keyless (label TEXT)
        """;

    private string ConnectionString(string dbName) => $"Data Source={dbName};Mode=Memory;Cache=Shared";

    protected override async Task CreateDatabaseAsync(string dbName)
    {
        _keepAlive = new SqliteConnection(ConnectionString(dbName));
        await _keepAlive.OpenAsync();
    }

    protected override async Task DropDatabaseAsync(string dbName)
    {
        if (_keepAlive is not null)
            await _keepAlive.DisposeAsync();
    }

    protected override IDbConnFactory CreateConnFactory(string dbName) => new SqliteDbConnFactory(ConnectionString(dbName));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_LessThan_FindsThePreviousDate(bool typedTarget)
    {
        await ExecAsync(
            """
            INSERT INTO time_entries (tenant_id, person_id, week, date) VALUES
                (1, 1, 1, '2026-10-01'),
                (1, 1, 2, '2026-11-03'),
                (1, 1, 3, '2026-11-09'),
                (1, 1, 4, '2026-11-10'),
                (1, 1, 5, NULL),
                (1, 2, 6, '2026-11-08')
            """);
        object target = typedTarget ? new DateOnly(2026, 11, 9) : "2026-11-09";
        object? found = null;
        QueryProbe.Body = async context =>
        {
            var rows = await context.QueryAsync(
                "time_entries",
                new Dictionary<string, object?> { ["person_id"] = 1, ["date"] = BifrostOperationContext.LessThan(target) },
                columns: new[] { "date" },
                limit: 1,
                sort: new[] { "date_desc" });
            found = rows.Single()["date"];
        };

        var result = await ExecuteAsync("{ queryProbe }", Caller());

        result.Errors.Should().BeNullOrEmpty(string.Join("; ", result.Errors?.Select(e => e.Message) ?? Array.Empty<string>()));
        found.Should().Be("2026-11-03");
    }

    [Fact]
    public async Task LockRow_IsRefusedLoudly_OnADialectWithNoRowLocks()
    {
        var wroteAfterLock = false;
        MutationProbe.Body = async context =>
        {
            await context.LockRowAsync("people", new Dictionary<string, object?> { ["id"] = 1 });
            wroteAfterLock = true;
        };

        var result = await ExecuteAsync("mutation { mutationProbe }", Caller());

        result.Errors.Should().ContainSingle().Which.Code.Should().Be(BifrostOperationContext.RowLockUnsupportedCode);
        wroteAfterLock.Should().BeFalse("an operation that asked for a lock must not run on without one");
    }
}

public sealed class PostgresServerOperationConcurrencyTests : ServerOperationRowLockTestBase
{
    private static string? Master => Environment.GetEnvironmentVariable("BIFROST_TEST_POSTGRES");

    protected override bool Available => Master is not null;
    protected override string TableSchema => "public";
    protected override string TryLockPersonOneSql => "SELECT id FROM people WHERE id = 1 FOR UPDATE NOWAIT";

    protected override string CreateTablesDdl => """
        CREATE TABLE people (id INT PRIMARY KEY, tenant_id INT NOT NULL, name VARCHAR(50) NOT NULL);
        CREATE TABLE time_entries (
            id INT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            tenant_id INT NOT NULL,
            person_id INT NOT NULL REFERENCES people(id),
            week INT NOT NULL,
            stopped_at TIMESTAMPTZ,
            note VARCHAR(50),
            secret VARCHAR(50)
        );
        CREATE UNIQUE INDEX one_running_entry ON time_entries (person_id) WHERE stopped_at IS NULL AND week = 1;
        CREATE TABLE week_locks (person_id INT NOT NULL, week INT NOT NULL, PRIMARY KEY (person_id, week));
        CREATE TABLE keyless (label VARCHAR(50));
        CREATE TABLE uuid_keyed (id UUID PRIMARY KEY, note TEXT);
        CREATE TABLE date_keyed (d DATE PRIMARY KEY, note TEXT);
        CREATE TABLE uuid_tenant (id UUID PRIMARY KEY, tenant_id UUID NOT NULL);
        CREATE TABLE trail (
            id SERIAL PRIMARY KEY,
            entity TEXT NOT NULL,
            entity_id TEXT NOT NULL,
            op TEXT NOT NULL,
            actor TEXT,
            changed_at TIMESTAMPTZ NOT NULL,
            before TEXT,
            after TEXT,
            changed_columns TEXT);
        CREATE TABLE measures (id INT PRIMARY KEY, reading REAL NOT NULL, price MONEY NOT NULL, at_time TIME NOT NULL)
        """;

    protected override async Task CreateDatabaseAsync(string dbName)
    {
        await using var conn = new Npgsql.NpgsqlConnection(Master);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE \"{dbName}\"";
        await cmd.ExecuteNonQueryAsync();
    }

    protected override async Task DropDatabaseAsync(string dbName)
    {
        await using var conn = new Npgsql.NpgsqlConnection(Master);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP DATABASE \"{dbName}\" WITH (FORCE)";
        await cmd.ExecuteNonQueryAsync();
    }

    protected override IDbConnFactory CreateConnFactory(string dbName)
        => new BifrostQL.Ngsql.PostgresDbConnFactory(
            new Npgsql.NpgsqlConnectionStringBuilder(Master!) { Database = dbName }.ConnectionString);

    [SkippableFact]
    public async Task ACaughtInvalidValueRead_RollsTheOperationBack_WithItsCode()
    {
        Skip.IfNot(Available, "test database environment variable not set");
        MutationProbe.Body = async context =>
        {
            await context.UpdateAsync("time_entries", new Dictionary<string, object?> { ["id"] = 1, ["note"] = "written" });
            await FluentActions.Awaiting(() => context.QueryAsync("time_entries",
                    new Dictionary<string, object?> { ["week"] = BifrostOperationContext.LessThan("not-a-number") }, new[] { "id" }))
                .Should().ThrowAsync<BifrostExecutionError>();
        };

        var result = await ExecuteAsync("mutation { mutationProbe }", Caller());

        result.Errors.Should().ContainSingle().Which.Code.Should().Be(BifrostExecutionError.InvalidValueCode);
        (await CountAsync("note = 'written'")).Should().Be(0);
        MutationsObserved.Tables.Should().BeEmpty();
    }

    private async Task<(int? Affected, ExecutionResult Result)> UpdateAsync(string table, string keyColumn, object key, string note)
    {
        int? affected = null;
        MutationProbe.Body = async context => affected = await context.UpdateAsync(table, new Dictionary<string, object?> { [keyColumn] = key, ["note"] = note });
        var result = await ExecuteAsync("mutation { mutationProbe }", Caller());
        return (affected, result);
    }

    private async Task<List<object?>> ReadIdsAsync(object? test)
    {
        var ids = new List<object?>();
        QueryProbe.Body = async context =>
            ids.AddRange((await context.QueryAsync("time_entries", new Dictionary<string, object?> { ["id"] = test }, new[] { "id" })).Select(row => row["id"]));
        (await ExecuteAsync("{ queryProbe }", Caller())).Errors.Should().BeNullOrEmpty();
        return ids;
    }

    public static IEnumerable<object[]> FractionalKeys => new[]
    {
        new object[] { 1.5m }, new object[] { 1.5d }, new object[] { 2.5m }, new object[] { 2.5d }, new object[] { 2.4m },
    };

    [SkippableTheory]
    [MemberData(nameof(FractionalKeys))]
    public async Task AFractionalKey_MatchesNoRow_InsteadOfRoundingOntoOne(object key)
    {
        Skip.IfNot(Available, "test database environment variable not set");

        var (affected, result) = await UpdateAsync("time_entries", "id", key, "rounded");

        result.Errors.Should().BeNullOrEmpty();
        affected.Should().Be(0, "no row has that id; casting it to integer would hit a neighbour");
        (await CountAsync("note = 'rounded'")).Should().Be(0);
    }

    [SkippableFact]
    public async Task ABooleanKey_IsAnError_InsteadOfAddressingRowOne()
    {
        Skip.IfNot(Available, "test database environment variable not set");

        var (_, result) = await UpdateAsync("time_entries", "id", true, "from-bool");

        result.Errors.Should().ContainSingle();
        (await CountAsync("note = 'from-bool'")).Should().Be(0);
    }

    [SkippableFact]
    public async Task ABigintKeyOutsideTheColumnsRange_MatchesNoRow()
    {
        Skip.IfNot(Available, "test database environment variable not set");

        var (affected, result) = await UpdateAsync("time_entries", "id", 1L << 40, "too-big");

        result.Errors.Should().BeNullOrEmpty();
        affected.Should().Be(0);
    }

    [SkippableTheory]
    [InlineData("1.5")]
    [InlineData("2.4")]
    [InlineData("abc")]
    public async Task TextThatIsNotAnIntegerKey_IsInvalidValue_AndChangesNothing(string key)
    {
        Skip.IfNot(Available, "test database environment variable not set");

        var (_, result) = await UpdateAsync("time_entries", "id", key, "from-text");

        result.Errors.Should().ContainSingle().Which.Code.Should().Be(BifrostExecutionError.InvalidValueCode);
        (await CountAsync("note = 'from-text'")).Should().Be(0);
    }

    [SkippableTheory]
    [InlineData(2)]
    [InlineData("2")]
    public async Task AWholeKey_AddressesItsRow_AsANumberOrAsText(object key)
    {
        Skip.IfNot(Available, "test database environment variable not set");

        var (affected, result) = await UpdateAsync("time_entries", "id", key, "whole");

        result.Errors.Should().BeNullOrEmpty();
        affected.Should().Be(1);
        (await CountAsync("id = 2 AND note = 'whole'")).Should().Be(1);
    }

    [SkippableFact]
    public async Task AUuidKey_AddressesItsRow_AsAGuidOrAsText()
    {
        Skip.IfNot(Available, "test database environment variable not set");
        var id = Guid.NewGuid();
        await ExecAsync($"INSERT INTO uuid_keyed (id) VALUES ('{id}')");

        (await UpdateAsync("uuid_keyed", "id", id, "typed")).Affected.Should().Be(1);
        (await UpdateAsync("uuid_keyed", "id", id.ToString(), "text")).Affected.Should().Be(1);
    }

    [SkippableFact]
    public async Task ADateTimeWithATime_DoesNotLandOnADateKey()
    {
        Skip.IfNot(Available, "test database environment variable not set");
        await ExecAsync("INSERT INTO date_keyed (d) VALUES ('2026-01-01')");

        (await UpdateAsync("date_keyed", "d", new DateTime(2026, 1, 1, 13, 30, 0), "wrong")).Affected.Should().Be(0, "13:30 on that day is not that date");
        (await UpdateAsync("date_keyed", "d", new DateOnly(2026, 1, 1), "right")).Affected.Should().Be(1);
        (await UpdateAsync("date_keyed", "d", "2026-01-01", "right")).Affected.Should().Be(1);
    }

    [SkippableFact]
    public async Task LockRow_WithAFractionalKey_FindsNoRow()
    {
        Skip.IfNot(Available, "test database environment variable not set");
        MutationProbe.Body = context => context.LockRowAsync("people", new Dictionary<string, object?> { ["id"] = 1.5m });

        var result = await ExecuteAsync("mutation { mutationProbe }", Caller());

        result.Errors.Should().ContainSingle().Which.Code.Should().Be(BifrostOperationContext.LockRowNotFoundCode);
    }

    [SkippableFact]
    public async Task DeleteAsync_WithAFractionalKey_DeletesNothing()
    {
        Skip.IfNot(Available, "test database environment variable not set");
        int? affected = null;
        MutationProbe.Body = async context => affected = await context.DeleteAsync("time_entries", new Dictionary<string, object?> { ["id"] = 2.4m });

        (await ExecuteAsync("mutation { mutationProbe }", Caller())).Errors.Should().BeNullOrEmpty();

        affected.Should().Be(0);
        (await CountAsync("id = 2")).Should().Be(1);
    }

    [SkippableFact]
    public async Task QueryAsync_ComparesANumberAsANumber()
    {
        Skip.IfNot(Available, "test database environment variable not set");

        (await ReadIdsAsync(1.5m)).Should().BeEmpty("no row has that id");
        // 1 is less than 1.4, whereas 1.4 cast to integer is 1.
        (await ReadIdsAsync(BifrostOperationContext.LessThan(1.4m))).Should().Equal(1);
        (await ReadIdsAsync(BifrostOperationContext.LessThan(2))).Should().Equal(1);
        (await ReadIdsAsync("2")).Should().Equal(2);
        (await ReadIdsAsync(BifrostOperationContext.LessThan("2"))).Should().Equal(1);
    }

    /// <summary>
    /// What leaving a non-text value bare costs on the types that had no comparison operator
    /// for it. Each read matched under the old unconditional cast.
    /// </summary>
    [SkippableFact]
    public async Task ANonTextValue_OnTheTypesWithoutANumericOperator()
    {
        Skip.IfNot(Available, "test database environment variable not set");
        await ExecAsync("INSERT INTO measures (id, reading, price, at_time) VALUES (1, 1.1, 2.50, '13:30')");

        async Task<ExecutionResult> ReadAsync(string column, object value)
        {
            QueryProbe.Body = context => context.QueryAsync("measures", new Dictionary<string, object?> { [column] = value }, new[] { "id" });
            return await ExecuteAsync("{ queryProbe }", Caller());
        }

        foreach (var reading in new object[] { 1.1d, 1.1m, 1.1f, "1.1" })
        {
            var count = -1;
            QueryProbe.Body = async context => count = (await context.QueryAsync("measures", new Dictionary<string, object?> { ["reading"] = reading }, new[] { "id" })).Count;
            (await ExecuteAsync("{ queryProbe }", Caller())).Errors.Should().BeNullOrEmpty();
            count.Should().Be(1, "a Float filter on a real column has to keep finding the value it reads back");
        }

        (await ReadAsync("price", "2.50")).Errors.Should().BeNullOrEmpty("money is text at the GraphQL surface");
        (await ReadAsync("at_time", "13:30")).Errors.Should().BeNullOrEmpty("time is text at the GraphQL surface");
        (await ReadAsync("price", 2.50m)).Errors.Should().ContainSingle("Postgres has no money = numeric operator; a loud refusal");
        (await ReadAsync("at_time", new DateTime(2026, 1, 1, 13, 30, 0))).Errors.Should().ContainSingle("Postgres has no time = timestamp operator; a loud refusal");
    }

    [SkippableFact]
    public async Task LockRow_RefusesAHistoryTable_LikeEveryOtherPath()
    {
        Skip.IfNot(Available, "test database environment variable not set");
        await LoadModelAsync(
            $":root {{ history-table: {TableSchema}.trail }}",
            $"{TableSchema}.week_locks {{ history: enabled }}");
        await ExecAsync("INSERT INTO trail (entity, entity_id, op, changed_at) VALUES ('week_locks', '1', 'insert', now())");
        MutationProbe.Body = context => context.LockRowAsync("trail", new Dictionary<string, object?> { ["id"] = 1 });

        var result = await ExecuteAsync("mutation { mutationProbe }", Caller());

        result.Errors.Should().ContainSingle().Which.Code.Should().Be(BifrostExecutionError.AccessDeniedCode,
            "a caller holding a lock on the trail would stall every tracked write");
    }

    /// <summary>
    /// A submitted tree carries its root key as text, and a polymorphic link's discriminator comes
    /// from metadata as text, so both comparisons have to cast them. A child's key is the parent's
    /// value as read, so it binds as text only when the parent's key is text and the child's is not.
    /// </summary>
    [SkippableFact]
    public async Task TreeSyncLoad_FindsARootAndItsChildren_ByATextUuidKey()
    {
        Skip.IfNot(Available, "test database environment variable not set");
        await ExecAsync("""
            CREATE TABLE sync_orders (id UUID PRIMARY KEY, title TEXT);
            CREATE TABLE sync_lines (id UUID PRIMARY KEY, order_id UUID NOT NULL REFERENCES sync_orders(id), label TEXT);
            CREATE TABLE sync_imports (code TEXT PRIMARY KEY, title TEXT);
            CREATE TABLE sync_notes (id UUID PRIMARY KEY, entity_kind INT NOT NULL, entity_id UUID NOT NULL, body TEXT);
            """);
        await LoadModelAsync(
            $"public.sync_notes {{ {MetadataKeys.Relationships.PolymorphicTypeCol}: entity_kind; {MetadataKeys.Relationships.PolymorphicIdCol}: entity_id; {MetadataKeys.Relationships.PolymorphicMap}: 1=sync_orders, 2=sync_imports }}");
        var (order, otherOrder) = (Guid.NewGuid(), Guid.NewGuid());
        await ExecAsync($"""
            INSERT INTO sync_orders (id, title) VALUES ('{order}', 'mine'), ('{otherOrder}', 'other');
            INSERT INTO sync_lines (id, order_id, label) VALUES ('{Guid.NewGuid()}', '{order}', 'my line'), ('{Guid.NewGuid()}', '{otherOrder}', 'other line');
            INSERT INTO sync_notes (id, entity_kind, entity_id, body) VALUES ('{Guid.NewGuid()}', 1, '{order}', 'my note'), ('{Guid.NewGuid()}', 2, '{order}', 'another kind');
            """);
        var import = Guid.NewGuid();
        await ExecAsync($"""
            INSERT INTO sync_imports (code, title) VALUES ('{import}', 'imported');
            INSERT INTO sync_notes (id, entity_kind, entity_id, body) VALUES ('{Guid.NewGuid()}', 2, '{import}', 'import note');
            """);
        var orders = Model.GetTableFromDbName("sync_orders");
        string LinkTo(string child) => orders.MultiLinks.Single(link => link.Value.ChildTable.DbName == child).Key;
        var submitted = new Dictionary<string, object?>
        {
            ["id"] = order.ToString(),
            [LinkTo("sync_lines")] = new List<object?>(),
            [LinkTo("sync_notes")] = new List<object?>(),
        };

        var loaded = await new TreeSyncStateLoader(ConnFactory.Dialect, Model, new Dictionary<string, object?>())
            .LoadAsync(orders, submitted, ConnFactory);

        loaded.Should().NotBeNull("the root's key arrives as text");
        loaded!["title"].Should().Be("mine");
        ((List<Dictionary<string, object?>>)loaded[LinkTo("sync_lines")]!).Should().ContainSingle()
            .Which["label"].Should().Be("my line");
        ((List<Dictionary<string, object?>>)loaded[LinkTo("sync_notes")]!).Should().ContainSingle()
            .Which["body"].Should().Be("my note");

        var imports = Model.GetTableFromDbName("sync_imports");
        var importNotes = imports.MultiLinks.Single(link => link.Value.ChildTable.DbName == "sync_notes").Key;
        var loadedImport = await new TreeSyncStateLoader(ConnFactory.Dialect, Model, new Dictionary<string, object?>())
            .LoadAsync(imports, new Dictionary<string, object?> { ["code"] = import.ToString(), [importNotes] = new List<object?>() }, ConnFactory);
        ((List<Dictionary<string, object?>>)loadedImport![importNotes]!).Should().ContainSingle()
            .Which["body"].Should().Be("import note", "the parent's text key meets a uuid child column");
    }

    private async Task<string?> ReadTextAsync(string sql)
    {
        await using var conn = ConnFactory.GetConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (string?)await cmd.ExecuteScalarAsync();
    }

    /// <summary>A delta stores its key as JSON, so a uuid key comes back to the undo engine as text.</summary>
    private async Task<(Guid Row, DeferredUndoEngine Engine)> SeedUuidUndoAsync(string changeSetState, string rowName)
    {
        await ExecAsync("""
            CREATE TABLE uuid_undo (id UUID PRIMARY KEY, name TEXT NOT NULL);
            CREATE TABLE change_sets (id BIGINT PRIMARY KEY, state TEXT NOT NULL, undo_window_expires_at TIMESTAMPTZ NOT NULL, requester TEXT, tenant TEXT, tables TEXT NOT NULL, created_at TIMESTAMPTZ NOT NULL, applied_at TIMESTAMPTZ, reversed_at TIMESTAMPTZ);
            CREATE TABLE change_set_deltas (id BIGINT PRIMARY KEY, change_set_id BIGINT NOT NULL, "table" TEXT NOT NULL, pk TEXT NOT NULL, op TEXT NOT NULL, inverse_op TEXT NOT NULL, before_image TEXT, after_image TEXT, created_at TIMESTAMPTZ NOT NULL);
            """);
        await LoadModelAsync();
        var row = Guid.NewGuid();
        await ExecAsync($"INSERT INTO uuid_undo (id, name) VALUES ('{row}', '{rowName}')");
        await ExecAsync($"INSERT INTO change_sets (id, state, undo_window_expires_at, tables, created_at) VALUES (1, '{changeSetState}', now() + interval '1 hour', '[]', now())");
        await ExecAsync($$"""
            INSERT INTO change_set_deltas (id, change_set_id, "table", pk, op, inverse_op, before_image, after_image, created_at)
            VALUES (1, 1, 'public.uuid_undo', '{"id":"{{row}}"}', 'update', 'restore',
                    '{"id":"{{row}}","name":"original"}', '{"id":"{{row}}","name":"edited"}', now())
            """);

        var endpoints = new PathCache<Inputs>();
        endpoints.AddLoader("/graphql", () => Task.FromResult(new Inputs(new Dictionary<string, object?>
        {
            ["model"] = Model, ["connFactory"] = ConnFactory,
        })));
        var mutations = new MutationIntentExecutor(endpoints, new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[] { new TenantMutationTransformer(), new SoftDeleteMutationTransformer(), new ConcurrencyMutationTransformer() },
        });
        return (row, new DeferredUndoEngine(Model, ConnFactory, mutations));
    }

    [SkippableFact]
    public async Task Undo_RestoresARowWithAUuidKey()
    {
        Skip.IfNot(Available, "test database environment variable not set");
        var (row, engine) = await SeedUuidUndoAsync("held", rowName: "edited");

        (await engine.UndoAsync(1, new Dictionary<string, object?>())).Should().Be(new DeferredUndoResult(1, 1, 0, false));

        (await ReadTextAsync($"SELECT name FROM uuid_undo WHERE id = '{row}'")).Should().Be("original");
    }

    [SkippableFact]
    public async Task ResumedUndo_FindsAnAlreadyRestoredRowByItsUuidKey()
    {
        Skip.IfNot(Available, "test database environment variable not set");
        var (_, engine) = await SeedUuidUndoAsync("undoing", rowName: "original");

        (await engine.UndoAsync(1, new Dictionary<string, object?>())).Should().Be(new DeferredUndoResult(1, 1, 0, false));

        (await ReadTextAsync("SELECT state FROM change_sets WHERE id = 1")).Should().Be("undone");
    }

    [SkippableFact]
    public async Task LockRow_ComparesAUuidTenantColumn_WithATextClaim()
    {
        Skip.IfNot(Available, "test database environment variable not set");
        await LoadModelAsync($"{TableSchema}.uuid_tenant {{ tenant-filter: tenant_id }}");
        var (mine, other) = (Guid.NewGuid(), Guid.NewGuid());
        var row = Guid.NewGuid();
        await ExecAsync($"INSERT INTO uuid_tenant (id, tenant_id) VALUES ('{row}', '{mine}')");
        MutationProbe.Body = context => context.LockRowAsync("uuid_tenant", new Dictionary<string, object?> { ["id"] = row });

        async Task<ExecutionResult> LockAsTenantAsync(Guid tenant)
        {
            var caller = Caller();
            caller["tenant_id"] = tenant.ToString();
            return await ExecuteAsync("mutation { mutationProbe }", caller);
        }

        (await LockAsTenantAsync(mine)).Errors.Should().BeNullOrEmpty();
        (await LockAsTenantAsync(other)).Errors.Should().ContainSingle().Which.Code.Should().Be(BifrostOperationContext.LockRowNotFoundCode);
    }

    [SkippableFact]
    public async Task LockRow_TakesAUuidKeyAsAGuid()
    {
        Skip.IfNot(Available, "test database environment variable not set");
        var id = Guid.NewGuid();
        await ExecAsync($"INSERT INTO uuid_keyed (id) VALUES ('{id}')");

        MutationProbe.Body = context => context.LockRowAsync("uuid_keyed", new Dictionary<string, object?> { ["id"] = id });

        (await ExecuteAsync("mutation { mutationProbe }", Caller())).Errors.Should().BeNullOrEmpty();
    }

    /// <summary>
    /// The race the lock exists to close, forced: both runs have read "nothing running" before
    /// either writes, so the second insert hits the partial unique index and fails instead of
    /// stopping the first run's entry. Keeps the suite above from passing on a fixture that
    /// cannot race.
    /// </summary>
    [SkippableFact]
    public async Task WithoutTheLock_TwoStartsBothReadNothingRunning_AndOneFailsOnTheIndex()
    {
        Skip.IfNot(Available, "test database environment variable not set");
        var bothRead = new Barrier(2);
        StartTimerOperation.TakesLock = false;
        StartTimerOperation.AfterRead = () => Task.Run(() => bothRead.SignalAndWait(TimeSpan.FromSeconds(30)));

        var results = await Task.WhenAll(
            Task.Run(() => ExecuteAsync("mutation { startTimer(personId: 1) }", Caller())),
            Task.Run(() => ExecuteAsync("mutation { startTimer(personId: 1) }", Caller())));

        results.SelectMany(result => result.Errors ?? new ExecutionErrors()).Should().ContainSingle()
            .Which.Code.Should().Be("CONFLICT");
        (await CountAsync("person_id = 1 AND stopped_at IS NOT NULL")).Should().Be(0, "neither run stopped anything");
    }
}

public sealed class MySqlServerOperationConcurrencyTests : ServerOperationRowLockTestBase
{
    protected override bool ScopedLockSkipsOtherRows => false;

    private static string? Master => Environment.GetEnvironmentVariable("BIFROST_TEST_MYSQL");
    private string? _dbName;

    protected override bool Available => Master is not null;
    protected override string TableSchema => _dbName!;
    protected override string TryLockPersonOneSql => "SELECT id FROM people WHERE id = 1 FOR UPDATE NOWAIT";

    protected override string CreateTablesDdl => """
        CREATE TABLE people (id INT PRIMARY KEY, tenant_id INT NOT NULL, name VARCHAR(50) NOT NULL);
        CREATE TABLE time_entries (
            id INT AUTO_INCREMENT PRIMARY KEY,
            tenant_id INT NOT NULL,
            person_id INT NOT NULL,
            week INT NOT NULL,
            stopped_at DATETIME(6) NULL,
            note VARCHAR(50) NULL,
            secret VARCHAR(50) NULL,
            CONSTRAINT fk_entry_person FOREIGN KEY (person_id) REFERENCES people(id)
        );
        CREATE TABLE week_locks (person_id INT NOT NULL, week INT NOT NULL, PRIMARY KEY (person_id, week));
        CREATE TABLE keyless (label VARCHAR(50))
        """;

    protected override async Task CreateDatabaseAsync(string dbName)
    {
        _dbName = dbName;
        await using var conn = new MySqlConnector.MySqlConnection(Master);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE `{dbName}`";
        await cmd.ExecuteNonQueryAsync();
    }

    protected override async Task DropDatabaseAsync(string dbName)
    {
        await using var conn = new MySqlConnector.MySqlConnection(Master);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP DATABASE `{dbName}`";
        await cmd.ExecuteNonQueryAsync();
    }

    protected override IDbConnFactory CreateConnFactory(string dbName)
        => new BifrostQL.MySql.MySqlDbConnFactory(
            new MySqlConnector.MySqlConnectionStringBuilder(Master!) { Database = dbName }.ConnectionString);

    /// <summary>
    /// Why the guide says to lock before reading: under InnoDB's REPEATABLE READ the first plain
    /// read fixes the snapshot, so a lock taken after it serializes the operation but the operation
    /// still reads rows as they were before it waited.
    /// </summary>
    [SkippableFact]
    public async Task ALockTakenAfterARead_StillReadsTheEarlierSnapshot()
    {
        Skip.IfNot(Available, "test database environment variable not set");
        var ofPersonOne = new Dictionary<string, object?> { ["person_id"] = 1 };
        var seenAfterTheLock = -1;
        MutationProbe.Body = async context =>
        {
            await context.QueryAsync("time_entries", ofPersonOne, new[] { "id" });
            await ExecAsync("INSERT INTO time_entries (tenant_id, person_id, week) VALUES (1, 1, 99)");
            await context.LockRowAsync("people", new Dictionary<string, object?> { ["id"] = 1 });
            seenAfterTheLock = (await context.QueryAsync("time_entries", ofPersonOne, new[] { "id" })).Count;
        };

        (await ExecuteAsync("mutation { mutationProbe }", Caller())).Errors.Should().BeNullOrEmpty();

        seenAfterTheLock.Should().Be(0, "the row committed before the lock is outside the snapshot the first read fixed");
    }
}

public sealed class SqlServerServerOperationConcurrencyTests : ServerOperationRowLockTestBase
{
    private static string? Master => Environment.GetEnvironmentVariable("BIFROST_TEST_SQLSERVER");

    protected override bool Available => Master is not null;
    protected override string TableSchema => "dbo";
    protected override string TryLockPersonOneSql => "SET LOCK_TIMEOUT 0; SELECT id FROM people WITH (UPDLOCK) WHERE id = 1";

    protected override string CreateTablesDdl => """
        CREATE TABLE people (id INT PRIMARY KEY, tenant_id INT NOT NULL, name VARCHAR(50) NOT NULL);
        CREATE TABLE time_entries (
            id INT IDENTITY(1,1) PRIMARY KEY,
            tenant_id INT NOT NULL,
            person_id INT NOT NULL REFERENCES people(id),
            week INT NOT NULL,
            stopped_at DATETIME2 NULL,
            note VARCHAR(50) NULL,
            secret VARCHAR(50) NULL
        );
        CREATE TABLE week_locks (person_id INT NOT NULL, week INT NOT NULL, PRIMARY KEY (person_id, week));
        CREATE TABLE keyless (label VARCHAR(50) NULL)
        """;

    protected override async Task CreateDatabaseAsync(string dbName)
    {
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(Master);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE [{dbName}]";
        await cmd.ExecuteNonQueryAsync();
    }

    protected override async Task DropDatabaseAsync(string dbName)
    {
        Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(Master);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{dbName}]";
        await cmd.ExecuteNonQueryAsync();
    }

    protected override IDbConnFactory CreateConnFactory(string dbName)
        => new BifrostQL.SqlServer.SqlServerDbConnFactory(
            new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(Master!) { InitialCatalog = dbName }.ConnectionString);
}
