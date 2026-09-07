using System.Data;
using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// One executed statement: its command text plus the parameter names actually
/// bound onto it. Both halves are needed to characterize a keyed write — the SQL
/// text names the columns, the parameter list names the two disjoint parameter
/// namespaces (sanitized column names and the generated <c>@p0..</c> the
/// transformer-injected predicate uses).
/// </summary>
internal sealed record CapturedSql(string Sql, IReadOnlyList<string> ParameterNames);

/// <summary>
/// Records every statement a resolver executes so a fact can assert on the
/// GENERATED SQL TEXT through the wired GraphQL path, not only on the response
/// shape (<c>.claude/rules/regression-test-non-vacuous.md</c>). Shared because
/// more than one selection-derived resolver needs the same evidence.
/// </summary>
internal sealed class SqlLoggingConnFactory : IDbConnFactory
{
    private readonly IDbConnFactory _inner;
    private readonly List<string> _log;
    private readonly List<CapturedSql>? _captured;

    public SqlLoggingConnFactory(IDbConnFactory inner, List<string> log)
        : this(inner, log, null)
    {
    }

    /// <summary>
    /// Also records the bound parameter names per statement, for facts that must
    /// assert the parameter namespace and not only the SQL text.
    /// </summary>
    public SqlLoggingConnFactory(IDbConnFactory inner, List<string> log, List<CapturedSql>? captured)
    {
        _inner = inner;
        _log = log;
        _captured = captured;
    }

    public DbConnection GetConnection() => new LoggingConnection(_inner.GetConnection(), _log, _captured);
    public ISqlDialect Dialect => _inner.Dialect;
    public ISchemaReader SchemaReader => _inner.SchemaReader;
    public ITypeMapper TypeMapper => _inner.TypeMapper;

    private sealed class LoggingConnection : DbConnection
    {
        private readonly DbConnection _inner;
        private readonly List<string> _log;
        private readonly List<CapturedSql>? _captured;

        public LoggingConnection(DbConnection inner, List<string> log, List<CapturedSql>? captured)
        {
            _inner = inner;
            _log = log;
            _captured = captured;
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
        protected override DbCommand CreateDbCommand() => new LoggingCommand(_inner.CreateCommand(), _log, _captured);
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
        private readonly List<CapturedSql>? _captured;

        public LoggingCommand(DbCommand inner, List<string> log, List<CapturedSql>? captured)
        {
            _inner = inner;
            _log = log;
            _captured = captured;
        }

        // Called at EXECUTE time, never at CommandText assignment: parameters are
        // bound after the text is set, so only here is the pair complete.
        private void Record()
        {
            _log.Add(_inner.CommandText);
            if (_captured is null) return;
            var names = new List<string>(_inner.Parameters.Count);
            foreach (DbParameter parameter in _inner.Parameters)
                names.Add(parameter.ParameterName);
            _captured.Add(new CapturedSql(_inner.CommandText, names));
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
        public override int ExecuteNonQuery() { Record(); return _inner.ExecuteNonQuery(); }
        public override object? ExecuteScalar() { Record(); return _inner.ExecuteScalar(); }
        public override void Prepare() => _inner.Prepare();
        protected override DbParameter CreateDbParameter() => _inner.CreateParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            Record();
            return _inner.ExecuteReader(behavior);
        }
        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        {
            Record();
            return _inner.ExecuteReaderAsync(behavior, cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
