using System.Data.Common;
using System.Security.Claims;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Integration.Test.Infrastructure;
using FluentAssertions;
using System.Reflection;

namespace BifrostQL.Integration.Test;

/// <summary>
/// <c>_rawQuery</c> runs client SQL in a scope the database itself refuses writes in
/// (PostgreSQL/MySQL read-only transaction, SQLite <c>query_only</c>) and always rolls
/// back (SQL Server, which has no read-only transaction). The lexical validator is the
/// secondary guard: a SELECT that calls a function which writes passes any lexer, so
/// only the database can stop it.
/// </summary>
public abstract class RawSqlReadOnlyExecutionTestsBase<TDatabase> : IClassFixture<DatabaseFixture<TDatabase>>
    where TDatabase : IIntegrationTestDatabase, new()
{
    private const string Role = "bifrost-raw-sql";
    protected readonly DatabaseFixture<TDatabase> Fixture;

    protected RawSqlReadOnlyExecutionTestsBase(DatabaseFixture<TDatabase> fixture) => Fixture = fixture;

    /// <summary>DDL for a function <c>{name}()</c> that deletes every row of <paramref name="table"/>, or null when the engine has no writing functions.</summary>
    protected abstract string? WritingFunctionSql(string name, string table);

    protected async Task ExecAsync(string sql)
    {
        await using var conn = Fixture.Database.ConnFactory.GetConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    protected async Task<string> CreateVictimTableAsync()
    {
        var table = $"raw_victims_{Guid.NewGuid():N}"[..24];
        await ExecAsync($"CREATE TABLE {table} (id INT PRIMARY KEY, name VARCHAR(20))");
        await ExecAsync($"INSERT INTO {table} (id, name) VALUES (1, 'alpha')");
        await ExecAsync($"INSERT INTO {table} (id, name) VALUES (2, 'beta')");
        return table;
    }

    protected async Task<List<string>> NamesAsync(string table)
    {
        await using var conn = Fixture.Database.ConnFactory.GetConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT name FROM {table} ORDER BY id";
        await using var reader = await cmd.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(0));
        return names;
    }

    [SkippableFact]
    public async Task Scope_WriteInsideIt_DoesNotSurvive()
    {
        Fixture.EnsureAvailable();
        var table = await CreateVictimTableAsync();
        var dialect = Fixture.Database.ConnFactory.Dialect;

        await using (var conn = Fixture.Database.ConnFactory.GetConnection())
        {
            await conn.OpenAsync();
            await using var scope = await dialect.BeginReadOnlyScopeAsync(conn, CancellationToken.None);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = scope.Transaction;
            cmd.CommandText = $"UPDATE {table} SET name = 'gone'";
            try
            {
                await cmd.ExecuteNonQueryAsync();
            }
            catch (DbException)
            {
                // Refused by a read-only engine; SQL Server runs it and the scope rolls back.
            }
        }

        (await NamesAsync(table)).Should().Equal("alpha", "beta");
    }

    /// <summary>False for SQL Server: it has no read-only transaction, only the scope's rollback.</summary>
    protected virtual bool EngineRefusesWrites => true;

    [SkippableFact]
    public async Task Scope_WriteInsideIt_IsRefusedByTheEngine()
    {
        // A rollback alone also keeps the rows, so this fact pins the read-only half:
        // the engine itself must refuse the statement.
        Fixture.EnsureAvailable();
        Skip.IfNot(EngineRefusesWrites, "The engine has no read-only transaction.");
        var table = await CreateVictimTableAsync();
        var dialect = Fixture.Database.ConnFactory.Dialect;

        await using var conn = Fixture.Database.ConnFactory.GetConnection();
        await conn.OpenAsync();
        await using var scope = await dialect.BeginReadOnlyScopeAsync(conn, CancellationToken.None);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = scope.Transaction;
        cmd.CommandText = $"UPDATE {table} SET name = 'gone'";

        var act = async () => await cmd.ExecuteNonQueryAsync();

        await act.Should().ThrowAsync<DbException>();
    }

    [SkippableFact]
    public async Task Scope_AfterDispose_ConnectionWritesAgain()
    {
        Fixture.EnsureAvailable();
        var table = await CreateVictimTableAsync();
        var factory = Fixture.Database.ConnFactory;

        await using (var conn = factory.GetConnection())
        {
            await conn.OpenAsync();
            await using (await factory.Dialect.BeginReadOnlyScopeAsync(conn, CancellationToken.None))
            {
            }

            // The same physical connection, as a pool would hand it out next.
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"UPDATE {table} SET name = 'kept' WHERE id = 1";
            (await cmd.ExecuteNonQueryAsync()).Should().Be(1);
        }

        (await NamesAsync(table)).Should().Equal("kept", "beta");
    }

    [SkippableFact]
    public async Task RawQuery_SelectReturnsRows()
    {
        Fixture.EnsureAvailable();
        var table = await CreateVictimTableAsync();

        var rows = await RawQueryAsync($"SELECT id, name FROM {table} ORDER BY id");

        rows.Select(r => r["name"]).Should().Equal("alpha", "beta");
    }

    [SkippableFact]
    public async Task RawQuery_SelectCallingAWritingFunction_LeavesRows()
    {
        Fixture.EnsureAvailable();
        var table = await CreateVictimTableAsync();
        var function = $"wipe_{table}";
        var ddl = WritingFunctionSql(function, table);
        Skip.If(ddl is null, "The engine has no functions that write.");
        await ExecAsync(ddl!);

        var act = async () => await RawQueryAsync($"SELECT {function}() AS wiped");

        await act.Should().ThrowAsync<BifrostExecutionError>();
        (await NamesAsync(table)).Should().Equal("alpha", "beta");
    }

    [SkippableFact]
    public async Task Executor_RowCapHit_CancelsAndReportsTruncation()
    {
        // The cap cancels the command so the reader's dispose does not drain the rest;
        // the cancelled command must still hand back the capped page, scoped or not.
        Fixture.EnsureAvailable();
        var table = await CreateVictimTableAsync();
        var factory = Fixture.Database.ConnFactory;
        var sql = $"SELECT id FROM {table} ORDER BY id";

        var scoped = await RawSqlExecutor.ExecuteReadOnlyAsync(factory, sql, null, 30, 1, CancellationToken.None);
        var unscoped = await RawSqlExecutor.ExecuteAsync(factory, sql, null, 30, 1);

        foreach (var result in new[] { scoped, unscoped })
        {
            result.Truncated.Should().BeTrue();
            result.Rows.Select(r => Convert.ToInt32(r[0])).Should().Equal(1);
        }
        (await NamesAsync(table)).Should().Equal("alpha", "beta");
    }

    protected async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = Fixture.Database.ConnFactory.GetConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    protected async Task<List<Dictionary<string, object?>>> RawQueryAsync(string sql)
    {
        var resolver = new RawSqlQueryResolver(Fixture.Database.DbModel);
        var context = new RawSqlFieldContext(Fixture.Database.ConnFactory, Fixture.Database.DbModel, sql);
        context.UserContext["user"] = new ClaimsPrincipal(
            new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, Role) }, "test"));
        var result = await resolver.ResolveAsync(context);
        return (List<Dictionary<string, object?>>)result!;
    }

    private sealed class RawSqlFieldContext : IBifrostFieldContext
    {
        private readonly string _sql;

        public RawSqlFieldContext(IDbConnFactory connFactory, IDbModel model, string sql)
        {
            _sql = sql;
            InputExtensions = new Dictionary<string, object?>
            {
                ["connFactory"] = connFactory,
                ["model"] = model,
                // _rawQuery never reaches the table executor; any call fails the test.
                ["tableReaderFactory"] = DispatchProxy.Create<ISqlExecutionManager, RawSqlUnusedExecutor>(),
            };
        }

        public IDictionary<string, object?> UserContext { get; } = new Dictionary<string, object?>();
        public IServiceProvider? RequestServices => null;
        public IDictionary<string, object?> InputExtensions { get; }
        public CancellationToken CancellationToken => CancellationToken.None;
        public string FieldName => "_rawQuery";
        public string? FieldAlias => null;
        public object? Source => null;
        public IReadOnlyList<object> Path => Array.Empty<object>();
        public bool HasSubFields => false;
        public object Document => null!;
        public object Variables => null!;
        public bool HasArgument(string name) => name == "sql";
        public T? GetArgument<T>(string name) => name == "sql" && _sql is T typed ? typed : default;
    }
}

/// <summary><c>_rawQuery</c> never reaches the table executor; any call fails the test.</summary>
public class RawSqlUnusedExecutor : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => throw new InvalidOperationException($"_rawQuery called the table executor ({targetMethod?.Name}).");
}

public sealed class PostgresRawSqlReadOnlyExecutionTests : RawSqlReadOnlyExecutionTestsBase<PostgresTestDatabase>
{
    public PostgresRawSqlReadOnlyExecutionTests(DatabaseFixture<PostgresTestDatabase> fixture) : base(fixture) { }

    protected override string WritingFunctionSql(string name, string table) =>
        $"CREATE FUNCTION {name}() RETURNS int LANGUAGE plpgsql AS $$ BEGIN DELETE FROM {table}; RETURN 1; END $$";

    [SkippableFact]
    public async Task RawQuery_FunctionAdvancingASequence_IsRefused()
    {
        // nextval is not transactional: a rollback alone leaves the sequence advanced.
        // Only the READ ONLY transaction refuses it ("cannot execute nextval() in a
        // read-only transaction"). The lexer denies a bare nextval, not one in a function.
        Fixture.EnsureAvailable();
        var seq = $"raw_seq_{Guid.NewGuid():N}"[..20];
        await ExecAsync($"CREATE SEQUENCE {seq}");
        await ExecAsync($"CREATE FUNCTION bump_{seq}() RETURNS bigint LANGUAGE sql AS 'SELECT nextval(''{seq}'')'");

        var act = async () => await RawQueryAsync($"SELECT bump_{seq}() AS n");

        await act.Should().ThrowAsync<BifrostExecutionError>();
        (await ScalarAsync($"SELECT is_called FROM {seq}")).Should().Be(false);
    }
}

public sealed class MySqlRawSqlReadOnlyExecutionTests : RawSqlReadOnlyExecutionTestsBase<MySqlTestDatabase>
{
    public MySqlRawSqlReadOnlyExecutionTests(DatabaseFixture<MySqlTestDatabase> fixture) : base(fixture) { }

    protected override string WritingFunctionSql(string name, string table) =>
        $"CREATE FUNCTION {name}() RETURNS INT DETERMINISTIC MODIFIES SQL DATA BEGIN DELETE FROM {table}; RETURN 1; END";
}

public sealed class SqlServerRawSqlReadOnlyExecutionTests : RawSqlReadOnlyExecutionTestsBase<SqlServerTestDatabase>
{
    public SqlServerRawSqlReadOnlyExecutionTests(DatabaseFixture<SqlServerTestDatabase> fixture) : base(fixture) { }

    // T-SQL functions cannot write; the scope's rollback and the ScriptDom parse cover the rest.
    protected override string? WritingFunctionSql(string name, string table) => null;

    protected override bool EngineRefusesWrites => false;
}

public sealed class SqliteRawSqlReadOnlyExecutionTests : RawSqlReadOnlyExecutionTestsBase<SqliteTestDatabase>
{
    public SqliteRawSqlReadOnlyExecutionTests(DatabaseFixture<SqliteTestDatabase> fixture) : base(fixture) { }

    protected override string? WritingFunctionSql(string name, string table) => null;
}
