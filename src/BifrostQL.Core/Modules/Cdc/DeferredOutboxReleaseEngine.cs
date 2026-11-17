using System.Data.Common;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Core.Modules.Cdc;

public static class DeferredOutboxColumns
{
    public const string ChangeSetId = "change_set_id";
    public const string State = "state";
    public const string PendingHold = "pending_hold";
    public const string Pending = "pending";
    public const string Suppressed = "suppressed";
}

/// <summary>
/// Atomically makes expired held change sets and their parked events dispatchable. One pass
/// claims at most <see cref="DefaultBatchSize"/> change sets, oldest id first, with two
/// set-based UPDATEs, so a backlog after downtime is worked off in short transactions instead
/// of one transaction holding 2N statements' locks.
/// </summary>
public sealed class DeferredOutboxReleaseEngine
{
    /// <summary>Change sets one <see cref="ReleaseOnceAsync"/> pass claims at most.</summary>
    public const int DefaultBatchSize = 100;

    private readonly IDbModel _model;
    private readonly IDbConnFactory _connections;
    private readonly int _batchSize;

    public DeferredOutboxReleaseEngine(IDbModel model, IDbConnFactory connections, int batchSize = DefaultBatchSize)
    {
        if (batchSize < 1) throw new ArgumentOutOfRangeException(nameof(batchSize), "The release batch size must be at least 1.");
        _model = model;
        _connections = connections;
        _batchSize = batchSize;
    }

    public async Task<int> ReleaseOnceAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var outboxName = _model.GetMetadataValue(MetadataKeys.Cdc.OutboxTable);
        var outbox = string.IsNullOrWhiteSpace(outboxName) ? null : ModelTableReference.Find(_model, outboxName);
        var changes = ModelTableReference.Find(_model, MetadataKeys.Deferred.ChangeSet.Table);
        if (outbox is null || changes is null || !outbox.Columns.Any(c => string.Equals(c.ColumnName, DeferredOutboxColumns.State, StringComparison.OrdinalIgnoreCase)))
            return 0;

        await using var connection = _connections.GetConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var candidates = await SelectExpiredAsync(connection, transaction, changes, now, cancellationToken);
            var released = candidates.Count == 0
                ? 0
                : await ClaimAndReleaseAsync(connection, transaction, changes, outbox, candidates, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return released;
        }
        catch
        {
            // Not on the request token: the rollback must finish even while the host stops.
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<int> ReleaseAsync(long changeSetId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var outboxName = _model.GetMetadataValue(MetadataKeys.Cdc.OutboxTable);
        var outbox = string.IsNullOrWhiteSpace(outboxName) ? null : ModelTableReference.Find(_model, outboxName);
        var changes = ModelTableReference.Find(_model, MetadataKeys.Deferred.ChangeSet.Table);
        if (outbox is null || changes is null) return 0;
        await using var connection = _connections.GetConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var d = _connections.Dialect;
        var update = $"UPDATE {d.TableReference(changes.TableSchema, changes.DbName)} SET {d.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.State)}=@released, {d.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.AppliedAt)}=@now WHERE {d.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.Id)}=@id AND {d.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.State)}=@held";
        if (await ExecuteAsync(connection, transaction, update, new() { ["released"] = "released", ["now"] = now, ["id"] = changeSetId, ["held"] = "held" }, cancellationToken) != 1) { await transaction.RollbackAsync(CancellationToken.None); return 0; }
        var released = await ReleaseEventsAsync(connection, transaction, outbox, changeSetId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return released;
    }

    private async Task<int> ReleaseEventsAsync(DbConnection connection, DbTransaction transaction, IDbTable outbox, object id, CancellationToken ct)
    {
        var d = _connections.Dialect;
        var sql = $"UPDATE {d.TableReference(outbox.TableSchema, outbox.DbName)} SET {d.EscapeIdentifier(DeferredOutboxColumns.State)}=@pending WHERE {d.EscapeIdentifier(DeferredOutboxColumns.ChangeSetId)}=@id AND {d.EscapeIdentifier(DeferredOutboxColumns.State)}=@held";
        return await ExecuteAsync(connection, transaction, sql, new() { ["pending"] = DeferredOutboxColumns.Pending, ["id"] = id, ["held"] = DeferredOutboxColumns.PendingHold }, ct);
    }

    // Up to _batchSize expired held change sets, oldest id first. Change sets held until approved
    // never expire into release, so they are skipped; the read pages past them by id so a run of
    // them cannot fill every batch and starve the expired change sets behind them.
    private async Task<List<object>> SelectExpiredAsync(DbConnection connection, DbTransaction transaction, IDbTable table, DateTimeOffset now, CancellationToken ct)
    {
        var d = _connections.Dialect;
        var tableRef = d.TableReference(table.TableSchema, table.DbName);
        var idColumn = d.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.Id);
        var ids = new List<object>();
        object? after = null;
        while (ids.Count < _batchSize)
        {
            var keyset = after is null ? "" : $" AND {idColumn}>@after";
            var select = $"SELECT {idColumn}, {d.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.Tables)} FROM {tableRef} WHERE {d.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.State)}=@held AND {d.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.UndoWindowExpiresAt)}<=@now{keyset}{d.Pagination(new[] { idColumn }, offset: null, limit: _batchSize)}";
            var read = 0;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction; command.CommandText = select; Add(command, "@held", "held"); Add(command, "@now", now);
            if (after is not null) Add(command, "@after", after);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                read++;
                after = reader.GetValue(0);
                var names = JsonSerializer.Deserialize<string[]>(reader.GetString(1)) ?? Array.Empty<string>();
                if (ids.Count < _batchSize && !names.Any(name => ModelTableReference.Find(_model, name) is { } source && Modules.Deferred.DeferredConfig.FromTable(source).EventHold == Modules.Deferred.DeferredEventHold.UntilApproved))
                    ids.Add(after);
            }
            if (read < _batchSize)
                break;
        }
        return ids;
    }

    // Claims the candidates that are still held and releases the parked events of exactly the
    // change sets this transaction claimed: one UPDATE each, not two per change set. A candidate
    // an undo claimed in between is no longer held, so neither it nor its events move.
    private async Task<int> ClaimAndReleaseAsync(DbConnection connection, DbTransaction transaction, IDbTable changes, IDbTable outbox,
        IReadOnlyList<object> candidates, DateTimeOffset now, CancellationToken ct)
    {
        var d = _connections.Dialect;
        var changesRef = d.TableReference(changes.TableSchema, changes.DbName);
        var idColumn = d.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.Id);
        var stateColumn = d.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.State);
        var appliedColumn = d.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.AppliedAt);
        var values = new Dictionary<string, object?> { ["released"] = "released", ["now"] = now, ["held"] = "held" };
        var idList = new List<string>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            values[$"id{i}"] = candidates[i];
            idList.Add($"@id{i}");
        }
        var ids = string.Join(", ", idList);

        // The claim moves only candidates still held: one an undo claimed in between is no
        // longer held and keeps its events parked.
        var claim = $"UPDATE {changesRef} SET {stateColumn}=@released, {appliedColumn}=@now WHERE {idColumn} IN ({ids}) AND {stateColumn}=@held";
        await ExecuteAsync(connection, transaction, claim, values, ct);

        // Events of the candidates that are now released. A change set released by an earlier
        // pass has no parked events left, and an undone one is not released, so this moves the
        // events of exactly the change sets claimed above.
        var release = $"UPDATE {d.TableReference(outbox.TableSchema, outbox.DbName)} SET {d.EscapeIdentifier(DeferredOutboxColumns.State)}=@pending WHERE {d.EscapeIdentifier(DeferredOutboxColumns.State)}=@pendingHold AND {d.EscapeIdentifier(DeferredOutboxColumns.ChangeSetId)} IN (SELECT {idColumn} FROM {changesRef} WHERE {idColumn} IN ({ids}) AND {stateColumn}=@released)";
        return await ExecuteAsync(connection, transaction, release,
            new Dictionary<string, object?>(values) { ["pending"] = DeferredOutboxColumns.Pending, ["pendingHold"] = DeferredOutboxColumns.PendingHold }, ct);
    }

    private static async Task<int> ExecuteAsync(DbConnection connection, DbTransaction transaction, string sql, Dictionary<string, object?> values, CancellationToken ct)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var pair in values) Add(command, "@" + pair.Key, pair.Value);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value ?? DBNull.Value; command.Parameters.Add(parameter);
    }
}
