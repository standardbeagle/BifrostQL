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

/// <summary>Atomically makes expired held change sets and their parked events dispatchable.</summary>
public sealed class DeferredOutboxReleaseEngine
{
    private readonly IDbModel _model;
    private readonly IDbConnFactory _connections;

    public DeferredOutboxReleaseEngine(IDbModel model, IDbConnFactory connections)
    {
        _model = model;
        _connections = connections;
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
            var ids = await ClaimExpiredAsync(connection, transaction, changes, now, cancellationToken);
            var released = 0;
            foreach (var id in ids) released += await ReleaseEventsAsync(connection, transaction, outbox, id, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return released;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
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
        if (await ExecuteAsync(connection, transaction, update, new() { ["released"] = "released", ["now"] = now, ["id"] = changeSetId, ["held"] = "held" }, cancellationToken) != 1) { await transaction.RollbackAsync(cancellationToken); return 0; }
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

    private async Task<List<object>> ClaimExpiredAsync(DbConnection connection, DbTransaction transaction, IDbTable table, DateTimeOffset now, CancellationToken ct)
    {
        var d = _connections.Dialect;
        var tableRef = d.TableReference(table.TableSchema, table.DbName);
        var select = $"SELECT {d.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.Id)}, {d.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.Tables)} FROM {tableRef} WHERE {d.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.State)}=@held AND {d.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.UndoWindowExpiresAt)}<=@now";
        var ids = new List<object>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction; command.CommandText = select; Add(command, "@held", "held"); Add(command, "@now", now);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var names = JsonSerializer.Deserialize<string[]>(reader.GetString(1)) ?? Array.Empty<string>();
                if (!names.Any(name => ModelTableReference.Find(_model, name) is { } source && Modules.Deferred.DeferredConfig.FromTable(source).EventHold == Modules.Deferred.DeferredEventHold.UntilApproved))
                    ids.Add(reader.GetValue(0));
            }
        }
        var claimed = new List<object>();
        foreach (var id in ids)
        {
            var update = $"UPDATE {tableRef} SET {d.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.State)}=@released, {d.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.AppliedAt)}=@now WHERE {d.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.Id)}=@id AND {d.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.State)}=@held";
            if (await ExecuteAsync(connection, transaction, update, new() { ["released"] = "released", ["now"] = now, ["id"] = id, ["held"] = "held" }, ct) == 1) claimed.Add(id);
        }
        return claimed;
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
