using System.Data;
using System.Data.Common;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using GraphQL.SystemTextJson;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// Finding M8: the <c>&lt;table&gt;Aggregate</c> resolver built a value column for
/// EVERY numeric column of the table whenever any op group (<c>_sum</c>/…) was
/// selected, and <c>QueryTransformerService</c> feeds all value columns to the
/// column read guard. A query aggregating only an allowed column was therefore
/// denied whenever the table had ANY policy-read-denied numeric column, and the
/// generated SQL aggregated columns the client never asked for.
///
/// The sibling guard tests (<see cref="QueryTransformerServiceReadGuardTests"/>)
/// build <c>GroupedAggregate</c> by hand, so they cannot manifest this bug — the
/// bug is in the resolver's derivation of value columns from the selection set,
/// which only exists on the wired GraphQL path.
/// </summary>
public sealed class GroupedAggregateSelectedColumnTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_grouped_aggregate_selected_test;Mode=Memory;Cache=Shared";

    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    private static readonly string[] Rules =
    {
        "main.orders { policy-actions: read; policy-read-deny: salary }",
    };

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS orders");
        await Exec(
            """
            CREATE TABLE orders (
                id INTEGER PRIMARY KEY,
                region TEXT NOT NULL,
                amount REAL NOT NULL,
                salary INTEGER NOT NULL
            )
            """);
        await Exec(
            """
            INSERT INTO orders(id, region, amount, salary) VALUES
                (1, 'east', 100, 250000),
                (2, 'east', 200, 260000)
            """);

        var factory = new SqliteDbConnFactory(ConnString);
        _model = await new DbModelLoader(factory, new MetadataLoader(Rules)).LoadAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<ExecutionResult> AggregateAsync(string query, List<string>? sqlLog = null)
    {
        var schema = DbSchema.FromModel(_model);
        IDbConnFactory factory = new SqliteDbConnFactory(ConnString);
        if (sqlLog != null)
            factory = new SqlLoggingConnFactory(factory, sqlLog);
        var transformerService = new QueryTransformerService(new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[]
            {
                new PolicyFilterTransformer(),
            },
        });

        return await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = query;
            options.UserContext = new Dictionary<string, object?>
            {
                ["user_id"] = "test-user",
                ["roles"] = new[] { "bifrost-admin" },
            };
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = factory,
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema, transformerService),
            });
        });
    }

    private const string PolicyReadDeniedMessage =
        "The query references a field that is not permitted by authorization policy.";

    [Fact]
    public async Task GroupedAggregate_SumOfAllowedColumn_DeniedSiblingNotAggregated_Succeeds()
    {
        // salary is policy-read-denied for this caller, but the query never selects
        // it. The resolver must not load (and the guard must not see) a column the
        // client did not ask to aggregate.
        var result = await AggregateAsync(
            "{ ordersAggregate(groupBy: [region]) { region _count _sum { amount } } }");

        result.Errors.Should().BeNullOrEmpty();
        var doc = JsonDocument.Parse(new GraphQLSerializer().Serialize(result));
        var east = doc.RootElement.GetProperty("data").GetProperty("ordersAggregate")
            .EnumerateArray().Single(g => g.GetProperty("region").GetString() == "east");
        east.GetProperty("_sum").GetProperty("amount").GetDouble().Should().Be(300);
    }

    [Fact]
    public async Task GroupedAggregate_SumOfDeniedColumn_IsRejected()
    {
        // Over-correction fence: deriving value columns from the selection must not
        // weaken the guard for a denied column the client DID select.
        var result = await AggregateAsync(
            "{ ordersAggregate(groupBy: [region]) { region _sum { salary } } }");

        result.Errors.Should().NotBeNullOrEmpty();
        result.Errors![0].InnerException.Should().BeOfType<BifrostExecutionError>()
            .Which.Message.Should().Be(PolicyReadDeniedMessage);
        new GraphQLSerializer().Serialize(result).Should().NotContain("250000");
    }

    [Fact]
    public async Task GroupedAggregate_MultipleOpGroups_SqlAggregatesOnlySelectedColumns()
    {
        // Acceptance: the generated SQL aggregates only the selected columns — the
        // union over op groups, each op restricted to its own sub-fields. Asserted
        // on the SQL text, not only via the policy outcome.
        var sql = new List<string>();
        var result = await AggregateAsync(
            "{ ordersAggregate(groupBy: [region]) { region _sum { amount } _avg { amount } _max { id } } }", sql);

        result.Errors.Should().BeNullOrEmpty();
        var statement = sql.Should().ContainSingle().Which;
        statement.Should().Contain("SUM(\"amount\")");
        statement.Should().Contain("AVG(\"amount\")");
        statement.Should().Contain("MAX(\"id\")");
        statement.Should().NotContain("salary");
        statement.Should().NotContain("MIN(");
        statement.Should().NotContain("SUM(\"id\")");
        statement.Should().NotContain("AVG(\"id\")");
        statement.Should().NotContain("MAX(\"amount\")");
    }

    [Fact]
    public async Task GroupedAggregate_FragmentWrappedSelection_ResolvesSameColumns()
    {
        // A named fragment spread and an inline fragment under an op group must
        // aggregate exactly the columns they select — and nothing else.
        var sql = new List<string>();
        var result = await AggregateAsync(
            """
            query { ordersAggregate(groupBy: [region]) { region _sum { ...Amount } _avg { ... on orders_aggregateFields { amount } } } }
            fragment Amount on orders_aggregateFields { amount }
            """, sql);

        result.Errors.Should().BeNullOrEmpty();
        var statement = sql.Should().ContainSingle().Which;
        statement.Should().Contain("SUM(\"amount\")");
        statement.Should().Contain("AVG(\"amount\")");
        statement.Should().NotContain("salary");
        var doc = JsonDocument.Parse(new GraphQLSerializer().Serialize(result));
        var east = doc.RootElement.GetProperty("data").GetProperty("ordersAggregate")
            .EnumerateArray().Single(g => g.GetProperty("region").GetString() == "east");
        east.GetProperty("_sum").GetProperty("amount").GetDouble().Should().Be(300);
        east.GetProperty("_avg").GetProperty("amount").GetDouble().Should().Be(150);
    }

    [Fact]
    public async Task GroupedAggregate_TypenameInsideOpGroup_IsNotAnAggregateColumn()
    {
        // __typename is a legal selection on every object type; it is not a column
        // and must be skipped, not rejected.
        var result = await AggregateAsync(
            "{ ordersAggregate(groupBy: [region]) { region _sum { __typename amount } } }");

        result.Errors.Should().BeNullOrEmpty();
        var doc = JsonDocument.Parse(new GraphQLSerializer().Serialize(result));
        var east = doc.RootElement.GetProperty("data").GetProperty("ordersAggregate")
            .EnumerateArray().Single(g => g.GetProperty("region").GetString() == "east");
        east.GetProperty("_sum").GetProperty("__typename").GetString().Should().Be("orders_aggregateFields");
        east.GetProperty("_sum").GetProperty("amount").GetDouble().Should().Be(300);
    }

    [Fact]
    public async Task GroupedAggregate_AliasedDuplicateSelection_ProjectsColumnOnce()
    {
        // `total: amount` and `amount` are the same schema field selected twice
        // (aliases and fragment overlap both produce this). The SQL must project
        // SUM(amount) once — a duplicate alias breaks the reader's column index.
        var sql = new List<string>();
        var result = await AggregateAsync(
            "{ ordersAggregate(groupBy: [region]) { region _sum { total: amount amount } } }", sql);

        result.Errors.Should().BeNullOrEmpty();
        var statement = sql.Should().ContainSingle().Which;
        statement.Split("SUM(\"amount\")").Should().HaveCount(2, "SUM(amount) must be projected exactly once");
        var doc = JsonDocument.Parse(new GraphQLSerializer().Serialize(result));
        var east = doc.RootElement.GetProperty("data").GetProperty("ordersAggregate")
            .EnumerateArray().Single(g => g.GetProperty("region").GetString() == "east");
        east.GetProperty("_sum").GetProperty("total").GetDouble().Should().Be(300);
        east.GetProperty("_sum").GetProperty("amount").GetDouble().Should().Be(300);
    }

    [Fact]
    public async Task GroupedAggregate_AliasedOpGroups_UnionSelectedColumns()
    {
        // The same op group selected under two aliases (or split across a fragment
        // and a flat selection) is two AST nodes for one schema field; every node's
        // sub-fields must be projected, not only the last one seen.
        var result = await AggregateAsync(
            """
            query { ordersAggregate(groupBy: [region]) { region s1: _sum { amount } s2: _sum { id } _sum { ...Id } _sum { amount } } }
            fragment Id on orders_aggregateFields { id }
            """);

        result.Errors.Should().BeNullOrEmpty();
        var doc = JsonDocument.Parse(new GraphQLSerializer().Serialize(result));
        var east = doc.RootElement.GetProperty("data").GetProperty("ordersAggregate")
            .EnumerateArray().Single(g => g.GetProperty("region").GetString() == "east");
        east.GetProperty("s1").GetProperty("amount").GetDouble().Should().Be(300);
        east.GetProperty("s2").GetProperty("id").GetDouble().Should().Be(3);
        east.GetProperty("_sum").GetProperty("id").GetDouble().Should().Be(3);
        east.GetProperty("_sum").GetProperty("amount").GetDouble().Should().Be(300);
    }

    /// <summary>
    /// Records every statement the resolver executes so a fact can assert on the
    /// generated SQL text through the wired GraphQL path.
    /// </summary>
    private sealed class SqlLoggingConnFactory : IDbConnFactory
    {
        private readonly IDbConnFactory _inner;
        private readonly List<string> _log;

        public SqlLoggingConnFactory(IDbConnFactory inner, List<string> log)
        {
            _inner = inner;
            _log = log;
        }

        public DbConnection GetConnection() => new LoggingConnection(_inner.GetConnection(), _log);
        public ISqlDialect Dialect => _inner.Dialect;
        public ISchemaReader SchemaReader => _inner.SchemaReader;
        public ITypeMapper TypeMapper => _inner.TypeMapper;
    }

    private sealed class LoggingConnection : DbConnection
    {
        private readonly DbConnection _inner;
        private readonly List<string> _log;

        public LoggingConnection(DbConnection inner, List<string> log)
        {
            _inner = inner;
            _log = log;
        }

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get => _inner.ConnectionString; set => _inner.ConnectionString = value; }
        public override string Database => _inner.Database;
        public override string DataSource => _inner.DataSource;
        public override string ServerVersion => _inner.ServerVersion;
        public override ConnectionState State => _inner.State;
        public override void ChangeDatabase(string databaseName) => _inner.ChangeDatabase(databaseName);
        public override void Close() => _inner.Close();
        public override void Open() => _inner.Open();
        public override Task OpenAsync(CancellationToken cancellationToken) => _inner.OpenAsync(cancellationToken);
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => _inner.BeginTransaction(isolationLevel);
        protected override DbCommand CreateDbCommand() => new LoggingCommand(_inner.CreateCommand(), _log);
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class LoggingCommand : DbCommand
    {
        private readonly DbCommand _inner;
        private readonly List<string> _log;

        public LoggingCommand(DbCommand inner, List<string> log)
        {
            _inner = inner;
            _log = log;
        }

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get => _inner.CommandText; set => _inner.CommandText = value; }
        public override int CommandTimeout { get => _inner.CommandTimeout; set => _inner.CommandTimeout = value; }
        public override CommandType CommandType { get => _inner.CommandType; set => _inner.CommandType = value; }
        public override bool DesignTimeVisible { get => _inner.DesignTimeVisible; set => _inner.DesignTimeVisible = value; }
        public override UpdateRowSource UpdatedRowSource { get => _inner.UpdatedRowSource; set => _inner.UpdatedRowSource = value; }
        protected override DbConnection? DbConnection { get => _inner.Connection; set => _inner.Connection = value; }
        protected override DbParameterCollection DbParameterCollection => _inner.Parameters;
        protected override DbTransaction? DbTransaction { get => _inner.Transaction; set => _inner.Transaction = value; }
        public override void Cancel() => _inner.Cancel();
        public override int ExecuteNonQuery() { _log.Add(_inner.CommandText); return _inner.ExecuteNonQuery(); }
        public override object? ExecuteScalar() { _log.Add(_inner.CommandText); return _inner.ExecuteScalar(); }
        public override void Prepare() => _inner.Prepare();
        protected override DbParameter CreateDbParameter() => _inner.CreateParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            _log.Add(_inner.CommandText);
            return _inner.ExecuteReader(behavior);
        }
        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        {
            _log.Add(_inner.CommandText);
            return _inner.ExecuteReaderAsync(behavior, cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
