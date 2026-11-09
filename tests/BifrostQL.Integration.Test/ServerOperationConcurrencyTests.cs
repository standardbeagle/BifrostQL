using System.Data.Common;
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
        })).LoadAsync();
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
    public async Task QueryAsync_NullTestOrSort_OnAReadDeniedColumn_IsRefusedLikeAGeneratedRead()
    {
        Skip.IfNot(Available, "test database environment variable not set");

        var filtered = await RunReadExpectingErrorAsync(context => context.QueryAsync("time_entries",
            new Dictionary<string, object?> { ["secret"] = BifrostOperationContext.NotNull }, columns: new[] { "week" }));
        var nullTested = await RunReadExpectingErrorAsync(context => context.QueryAsync("time_entries",
            new Dictionary<string, object?> { ["secret"] = null }, columns: new[] { "week" }));
        var sorted = await RunReadExpectingErrorAsync(context => context.QueryAsync("time_entries",
            columns: new[] { "week" }, sort: new[] { "secret_desc" }));

        foreach (var error in new[] { filtered, nullTested, sorted })
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

    /// <summary>The host composition: the same transformer chains a generated field runs.</summary>
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
    public async Task LockRow_ReachesARowOutsideTheCallersTenant()
    {
        Skip.IfNot(Available, "test database environment variable not set");
        MutationProbe.Body = context => context.LockRowAsync("people", new Dictionary<string, object?> { ["id"] = 3 });

        var result = await ExecuteAsync("mutation { mutationProbe }", Caller(tenantId: 1));

        result.Errors.Should().BeNullOrEmpty("operation code is trusted with its keys, so the lock takes no caller scope");
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

    private async Task<bool> TryLockPersonOneAsync()
    {
        await using var conn = ConnFactory.GetConnection();
        await conn.OpenAsync();
        await using var transaction = await conn.BeginTransactionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = TryLockPersonOneSql;
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
        CREATE TABLE uuid_keyed (id UUID PRIMARY KEY)
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
