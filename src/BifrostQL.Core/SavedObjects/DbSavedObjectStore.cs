using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.SavedObjects;

/// <summary>
/// DB-backed saved-object store for hosted deployments: one dedicated table
/// (default <c>_bifrost_saved_objects</c>, keyed by (type, id)) provisioned on first
/// use via the dialect's portable <c>CREATE TABLE IF NOT EXISTS</c>. All SQL is
/// parameterized and all identifiers dialect-escaped. Updates use a conditional
/// <c>WHERE version = @expected</c> so a lost update is caught race-free at the
/// database, not by a read-then-write window.
/// </summary>
public sealed class DbSavedObjectStore : ISavedObjectStore
{
    public const string DefaultTableName = "_bifrost_saved_objects";

    private readonly IDbConnFactory _connFactory;
    private readonly ISqlDialect _dialect;
    private readonly string _tableName;
    private readonly int _maxDefinitionBytes;
    private readonly int _timeoutSeconds;

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    private static readonly IReadOnlyList<SqlColumnDefinition> Columns = new[]
    {
        new SqlColumnDefinition("type", SqlColumnKind.Text, Nullable: false, PrimaryKey: true),
        new SqlColumnDefinition("id", SqlColumnKind.Text, Nullable: false, PrimaryKey: true),
        new SqlColumnDefinition("name", SqlColumnKind.Text, Nullable: false),
        new SqlColumnDefinition("folder", SqlColumnKind.Text, Nullable: true),
        new SqlColumnDefinition("definition", SqlColumnKind.Text, Nullable: false),
        new SqlColumnDefinition("version", SqlColumnKind.Int, Nullable: false),
    };

    public DbSavedObjectStore(
        IDbConnFactory connFactory,
        string? tableName = null,
        int maxDefinitionBytes = SavedObjectJson.DefaultMaxDefinitionBytes,
        int timeoutSeconds = 30)
    {
        _connFactory = connFactory ?? throw new ArgumentNullException(nameof(connFactory));
        _dialect = connFactory.Dialect;
        _tableName = string.IsNullOrWhiteSpace(tableName) ? DefaultTableName : tableName;
        _maxDefinitionBytes = maxDefinitionBytes;
        _timeoutSeconds = timeoutSeconds;
    }

    private string TableRef => _dialect.TableReference(null, _tableName);
    private string Col(string name) => _dialect.EscapeIdentifier(name);

    private async Task EnsureTableAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
            return;
        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
                return;
            var ddl = _dialect.CreateTableIfNotExistsSql(TableRef, Columns);
            await RawSqlExecutor.ExecuteAsync(_connFactory, ddl, null, _timeoutSeconds, maxRows: 0, cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<IReadOnlyList<SavedObject>> ListAsync(SavedObjectType? type, CancellationToken cancellationToken = default)
    {
        await EnsureTableAsync(cancellationToken);

        var sql = $"SELECT {Col("id")}, {Col("type")}, {Col("name")}, {Col("folder")}, {Col("definition")}, {Col("version")} FROM {TableRef}";
        IReadOnlyDictionary<string, object?>? parameters = null;
        if (type.HasValue)
        {
            sql += $" WHERE {Col("type")} = @type";
            parameters = new Dictionary<string, object?> { ["type"] = TypeValue(type.Value) };
        }

        var result = await RawSqlExecutor.ExecuteAsync(_connFactory, sql, parameters, _timeoutSeconds, maxRows: 100_000, cancellationToken);
        return result.Rows.Select(MapRow).ToList();
    }

    public async Task<SavedObject?> GetAsync(SavedObjectType type, string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await EnsureTableAsync(cancellationToken);

        var sql = $"SELECT {Col("id")}, {Col("type")}, {Col("name")}, {Col("folder")}, {Col("definition")}, {Col("version")} FROM {TableRef} WHERE {Col("type")} = @type AND {Col("id")} = @id";
        var parameters = new Dictionary<string, object?> { ["type"] = TypeValue(type), ["id"] = id };
        var result = await RawSqlExecutor.ExecuteAsync(_connFactory, sql, parameters, _timeoutSeconds, maxRows: 1, cancellationToken);
        return result.Rows.Count == 0 ? null : MapRow(result.Rows[0]);
    }

    public async Task<SavedObject> PutAsync(SavedObject obj, CancellationToken cancellationToken = default)
    {
        SavedObjectJson.Validate(obj, _maxDefinitionBytes);
        await EnsureTableAsync(cancellationToken);

        var existing = await GetAsync(obj.Type, obj.Id, cancellationToken);
        if (existing == null)
        {
            if (obj.Version != 0)
                throw new SavedObjectVersionConflictException(obj.Type, obj.Id, obj.Version, 0);
            return await InsertAsync(obj, cancellationToken);
        }

        if (obj.Version != existing.Version)
            throw new SavedObjectVersionConflictException(obj.Type, obj.Id, obj.Version, existing.Version);

        return await UpdateAsync(obj, cancellationToken);
    }

    private async Task<SavedObject> InsertAsync(SavedObject obj, CancellationToken cancellationToken)
    {
        var sql = $"INSERT INTO {TableRef} ({Col("type")}, {Col("id")}, {Col("name")}, {Col("folder")}, {Col("definition")}, {Col("version")})"
                + " VALUES (@type, @id, @name, @folder, @definition, @version)";
        var parameters = new Dictionary<string, object?>
        {
            ["type"] = TypeValue(obj.Type),
            ["id"] = obj.Id,
            ["name"] = obj.Name,
            ["folder"] = obj.Folder,
            ["definition"] = obj.Definition.GetRawText(),
            ["version"] = 1,
        };
        try
        {
            await RawSqlExecutor.ExecuteAsync(_connFactory, sql, parameters, _timeoutSeconds, maxRows: 0, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The pre-insert existence check is check-then-act: a concurrent create for
            // the same (type, id) races past it and the loser's INSERT violates the
            // primary key. Rather than sniff dialect-specific constraint codes, re-read:
            // if the row now exists the failure WAS that race — surface it as the same
            // version conflict the update path raises (409), not an unhandled 500. If it
            // does not exist, the failure was something real, so rethrow unchanged.
            var current = await GetAsync(obj.Type, obj.Id, cancellationToken);
            if (current != null)
                throw new SavedObjectVersionConflictException(obj.Type, obj.Id, obj.Version, current.Version);
            throw;
        }
        return obj with { Version = 1 };
    }

    private async Task<SavedObject> UpdateAsync(SavedObject obj, CancellationToken cancellationToken)
    {
        // Conditional on the version so a concurrent writer that already bumped the row
        // makes this affect zero rows — a lost update caught at the DB, not a stale read.
        var sql = $"UPDATE {TableRef} SET {Col("name")} = @name, {Col("folder")} = @folder, {Col("definition")} = @definition, {Col("version")} = {Col("version")} + 1"
                + $" WHERE {Col("type")} = @type AND {Col("id")} = @id AND {Col("version")} = @expected";
        var parameters = new Dictionary<string, object?>
        {
            ["type"] = TypeValue(obj.Type),
            ["id"] = obj.Id,
            ["name"] = obj.Name,
            ["folder"] = obj.Folder,
            ["definition"] = obj.Definition.GetRawText(),
            ["expected"] = obj.Version,
        };
        var result = await RawSqlExecutor.ExecuteAsync(_connFactory, sql, parameters, _timeoutSeconds, maxRows: 0, cancellationToken);
        if (result.RowsAffected == 0)
        {
            // The row moved under us between the read and the write.
            var current = await GetAsync(obj.Type, obj.Id, cancellationToken);
            throw new SavedObjectVersionConflictException(obj.Type, obj.Id, obj.Version, current?.Version ?? -1);
        }
        return obj with { Version = obj.Version + 1 };
    }

    public async Task DeleteAsync(SavedObjectType type, string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await EnsureTableAsync(cancellationToken);

        var sql = $"DELETE FROM {TableRef} WHERE {Col("type")} = @type AND {Col("id")} = @id";
        var parameters = new Dictionary<string, object?> { ["type"] = TypeValue(type), ["id"] = id };
        await RawSqlExecutor.ExecuteAsync(_connFactory, sql, parameters, _timeoutSeconds, maxRows: 0, cancellationToken);
    }

    private static string TypeValue(SavedObjectType type) => type.ToString().ToLowerInvariant();

    /// <summary>Maps a row projected as (id, type, name, folder, definition, version) to a <see cref="SavedObject"/>.</summary>
    private static SavedObject MapRow(object?[] row) => new()
    {
        Id = Convert.ToString(row[0])!,
        Type = SavedObjectJson.ParseType(Convert.ToString(row[1])!),
        Name = Convert.ToString(row[2])!,
        Folder = row[3] as string,
        Definition = SavedObjectJson.ParseDefinition(Convert.ToString(row[4])!),
        Version = Convert.ToInt32(row[5]),
    };
}
