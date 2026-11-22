using BifrostQL.Integration.Test.Infrastructure;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.ComputedColumns;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Ngsql;
using BifrostQL.SqlServer;
using GraphQL.Types;
using GraphQL;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BifrostQL.Integration.Test.Perf;

/// <summary>
/// A measurement fact: it prints numbers and asserts only that the run completed. It runs
/// only when <c>BIFROST_PERF=1</c>, so the epic and release tiers never pay for the seeding
/// and never flake on a slow machine.
/// </summary>
public sealed class PerfFactAttribute : SkippableFactAttribute
{
    public PerfFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("BIFROST_PERF") != "1")
            Skip = "Measurement only: set BIFROST_PERF=1 to run.";
    }
}

public enum PerfEngine { Postgres, SqlServer }

/// <summary>A scratch database on one engine, dropped on dispose.</summary>
public sealed class PerfDatabase : IAsyncDisposable
{
    private readonly PerfEngine _engine;
    private readonly string _master;
    private readonly string _name;

    public string ConnectionString { get; }
    public IDbConnFactory Factory { get; }

    private PerfDatabase(PerfEngine engine, string master, string name, string connectionString, IDbConnFactory factory)
    {
        _engine = engine;
        _master = master;
        _name = name;
        ConnectionString = connectionString;
        Factory = factory;
    }

    public static string? MasterFor(PerfEngine engine) => Environment.GetEnvironmentVariable(
        engine == PerfEngine.Postgres ? "BIFROST_TEST_POSTGRES" : "BIFROST_TEST_SQLSERVER");

    public static async Task<PerfDatabase> CreateAsync(PerfEngine engine, string prefix, string ddl)
    {
        var master = MasterFor(engine) ?? throw new InvalidOperationException($"{engine} test connection string not set");
        var name = $"{prefix}_{Guid.NewGuid():N}";
        string connectionString;
        IDbConnFactory factory;
        if (engine == PerfEngine.Postgres)
        {
            await ExecAsync(new NpgsqlConnection(master), $"CREATE DATABASE \"{name}\"");
            connectionString = new NpgsqlConnectionStringBuilder(master) { Database = name }.ConnectionString;
            factory = new PostgresDbConnFactory(connectionString);
        }
        else
        {
            await ExecAsync(new SqlConnection(master), $"CREATE DATABASE [{name}]");
            connectionString = new SqlConnectionStringBuilder(master) { InitialCatalog = name }.ConnectionString;
            factory = new SqlServerDbConnFactory(connectionString);
        }
        var db = new PerfDatabase(engine, master, name, connectionString, factory);
        await db.ExecAsync(ddl);
        return db;
    }

    public Task ExecAsync(string sql) => ExecAsync(Factory.GetConnection(), sql);

    private static async Task ExecAsync(DbConnection conn, string sql)
    {
        await using (conn)
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 600;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_engine == PerfEngine.Postgres)
        {
            await TestDatabaseDrop.PostgresAsync(_master, _name);
        }
        else
        {
            await TestDatabaseDrop.SqlServerAsync(_master, _name);
        }
    }
}

/// <summary>
/// Runs GraphQL documents through the production read path (schema, SqlExecutionManager,
/// computed-column providers) and counts the commands each request executes.
/// </summary>
public sealed class PerfGraphQlRunner : IAsyncDisposable
{
    private readonly IDbModel _model;
    private readonly ISchema _schema;
    private readonly ServiceProvider _services;
    private readonly CommandCountingConnFactory _factory;
    private readonly DocumentExecuter _executer = new();

    private PerfGraphQlRunner(IDbModel model, IDbConnFactory inner, ServiceProvider services)
    {
        _model = model;
        _schema = DbSchema.FromModel(model);
        _services = services;
        _factory = new CommandCountingConnFactory(inner);
    }

    public CommandCountingConnFactory Factory => _factory;

    public static async Task<PerfGraphQlRunner> CreateAsync(
        IDbConnFactory factory, IReadOnlyList<string> metadata, params IComputedColumnProvider[] providers)
    {
        var model = await new DbModelLoader(factory, new MetadataLoader(metadata.ToArray())).LoadAsync();
        var services = new ServiceCollection();
        foreach (var provider in providers)
            services.AddSingleton(provider);
        services.AddSingleton<IComputedColumnProviders>(sp => new ComputedColumnProviders(sp.GetServices<IComputedColumnProvider>()));
        return new PerfGraphQlRunner(model, factory, services.BuildServiceProvider());
    }

    public async Task<ExecutionResult> RunAsync(string query)
    {
        var result = await _executer.ExecuteAsync(options =>
        {
            options.Schema = _schema;
            options.Query = query;
            options.RequestServices = _services;
            options.UserContext = new Dictionary<string, object?>();
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = _factory,
                ["model"] = _model,
                // One manager per request, as production builds it: it memoizes the parsed document.
                ["tableReaderFactory"] = new SqlExecutionManager(_model, _schema, NullQueryTransformerService.Instance),
            });
        });
        if (result.Errors is { Count: > 0 })
            throw new InvalidOperationException(result.Errors[0].Message);
        return result;
    }

    /// <summary>Median and p90 wall time over <paramref name="runs"/> after <paramref name="warmup"/> runs.</summary>
    public async Task<(double MedianMs, double P90Ms, long CommandsPerRequest)> MeasureAsync(string query, int warmup, int runs)
    {
        for (var i = 0; i < warmup; i++)
            await RunAsync(query);
        var before = _factory.Commands;
        await RunAsync(query);
        var commands = _factory.Commands - before;
        var samples = new double[runs];
        for (var i = 0; i < runs; i++)
        {
            var sw = Stopwatch.StartNew();
            await RunAsync(query);
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(samples);
        return (samples[runs / 2], samples[(int)(runs * 0.9)], commands);
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();
}

/// <summary>Counts every command executed through its connections, thread-safely.</summary>
public sealed class CommandCountingConnFactory : IDbConnFactory
{
    private readonly IDbConnFactory _inner;
    private long _commands;

    public CommandCountingConnFactory(IDbConnFactory inner) => _inner = inner;

    public long Commands => Interlocked.Read(ref _commands);
    public DbConnection GetConnection() => new CountingConnection(_inner.GetConnection(), this);
    public ISqlDialect Dialect => _inner.Dialect;
    public ISchemaReader SchemaReader => _inner.SchemaReader;
    public ITypeMapper TypeMapper => _inner.TypeMapper;

    private void Count() => Interlocked.Increment(ref _commands);

    private sealed class CountingConnection(DbConnection inner, CommandCountingConnFactory owner) : DbConnection
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get => inner.ConnectionString; set => inner.ConnectionString = value; }
        public override string Database => inner.Database;
        public override string DataSource => inner.DataSource;
        public override string ServerVersion => inner.ServerVersion;
        public override ConnectionState State => inner.State;
        public override void ChangeDatabase(string databaseName) => inner.ChangeDatabase(databaseName);
        public override void Close() => inner.Close();
        public override void Open() => inner.Open();
        public override Task OpenAsync(CancellationToken cancellationToken) => inner.OpenAsync(cancellationToken);
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => inner.BeginTransaction(isolationLevel);
        protected override DbCommand CreateDbCommand() => new CountingCommand(inner.CreateCommand(), owner);
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class CountingCommand(DbCommand inner, CommandCountingConnFactory owner) : DbCommand
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get => inner.CommandText; set => inner.CommandText = value; }
        public override int CommandTimeout { get => inner.CommandTimeout; set => inner.CommandTimeout = value; }
        public override CommandType CommandType { get => inner.CommandType; set => inner.CommandType = value; }
        public override bool DesignTimeVisible { get => inner.DesignTimeVisible; set => inner.DesignTimeVisible = value; }
        public override UpdateRowSource UpdatedRowSource { get => inner.UpdatedRowSource; set => inner.UpdatedRowSource = value; }
        protected override DbConnection? DbConnection { get => inner.Connection; set => inner.Connection = value; }
        protected override DbParameterCollection DbParameterCollection => inner.Parameters;
        protected override DbTransaction? DbTransaction { get => inner.Transaction; set => inner.Transaction = value; }
        public override void Cancel() => inner.Cancel();
        public override int ExecuteNonQuery() { owner.Count(); return inner.ExecuteNonQuery(); }
        public override object? ExecuteScalar() { owner.Count(); return inner.ExecuteScalar(); }
        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) { owner.Count(); return inner.ExecuteNonQueryAsync(cancellationToken); }
        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken) { owner.Count(); return inner.ExecuteScalarAsync(cancellationToken); }
        public override void Prepare() => inner.Prepare();
        protected override DbParameter CreateDbParameter() => inner.CreateParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) { owner.Count(); return inner.ExecuteReader(behavior); }
        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        {
            owner.Count();
            return inner.ExecuteReaderAsync(behavior, cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
