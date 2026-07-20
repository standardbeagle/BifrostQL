using System.Data.Common;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Modules.Cdc;

namespace BifrostQL.Core.Modules.Deferred;

/// <summary>Replays one held change set through the normal mutation pipeline.</summary>
public sealed class DeferredUndoEngine
{
    private const string Held = "held";
    private const string Undone = "undone";
    private const string Partial = "partial";
    private const string Undoing = "undoing";
    private const string Released = "released";
    private readonly IDbModel _model;
    private readonly IDbConnFactory _connections;
    private readonly IMutationIntentExecutor _mutations;
    private readonly Func<DateTimeOffset> _clock;

    public DeferredUndoEngine(IDbModel model, IDbConnFactory connections, IMutationIntentExecutor mutations,
        Func<DateTimeOffset>? clock = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _mutations = mutations ?? throw new ArgumentNullException(nameof(mutations));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<DeferredUndoResult> UndoAsync(long changeSetId, IDictionary<string, object?> userContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userContext);
        var changeSet = await LoadChangeSetAsync(changeSetId, cancellationToken);
        if (string.Equals(changeSet.State, Undone, StringComparison.OrdinalIgnoreCase))
            return new DeferredUndoResult(changeSetId, 0, 0, true);
        var resuming = string.Equals(changeSet.State, Undoing, StringComparison.OrdinalIgnoreCase);
        var released = string.Equals(changeSet.State, Released, StringComparison.OrdinalIgnoreCase);
        if (!resuming && !released && !string.Equals(changeSet.State, Held, StringComparison.OrdinalIgnoreCase))
            throw new BifrostExecutionError("The deferred change set is not available for undo.");
        // Lifecycle is authoritative. A durable undo claim remains resumable after the
        // original window, and a released event must be compensatable after dispatch.
        // Only an ordinary, never-claimed held change is closed by expiry.
        if (!resuming && !released && changeSet.ExpiresAt <= _clock())
            throw new BifrostExecutionError("The deferred change set undo window has expired.");
        if (!resuming && !await TryClaimUndoAsync(changeSetId, changeSet.State, cancellationToken))
            throw new BifrostExecutionError("The deferred change set is not available for undo.");

        var undone = 0;
        var conflicts = 0;
        foreach (var delta in await LoadDeltasAsync(changeSetId, cancellationToken))
        {
            try
            {
                if (resuming && await InverseAlreadyAppliedAsync(delta, cancellationToken))
                {
                    undone++;
                    continue;
                }
                var result = await _mutations.ExecuteAsync(ToInverseIntent(delta, userContext), cancellationToken);
                // Every inverse must report exactly one affected row. A null count
                // has no success semantics; zero includes policy/tenant narrowing,
                // and any other count means the addressed inverse was not singular.
                if (result.AffectedRows == 1)
                    undone++;
                else
                    conflicts++;
            }
            catch (BifrostExecutionError error) when (string.Equals(error.ErrorCode, "CONFLICT", StringComparison.Ordinal))
            {
                conflicts++;
            }
        }

        await SettleHeldEventsAsync(changeSetId, cancellationToken);
        await SetStateAsync(changeSetId, conflicts == 0 ? Undone : Partial, cancellationToken);
        return new DeferredUndoResult(changeSetId, undone, conflicts, false);
    }

    private MutationIntent ToInverseIntent(Delta delta, IDictionary<string, object?> userContext)
    {
        var table = FindTable(delta.Table);
        var key = DeserializeObject(delta.Pk, "primary key");
        var restoringSoftDeletedRow = string.Equals(delta.Op, "update", StringComparison.Ordinal);
        var data = delta.InverseOp switch
        {
            "delete" => DeserializeObject(delta.AfterImage, "post-write image"),
            "restore" => DeserializeObject(delta.BeforeImage, "before image"),
            _ => throw new BifrostExecutionError("The deferred delta has an invalid inverse operation."),
        };
        if (delta.InverseOp != "restore" || restoringSoftDeletedRow)
        {
            foreach (var (name, _) in key)
                data.Remove(name);
        }

        var token = table.GetMetadataValue(MetadataKeys.Concurrency.Token);
        if (!string.IsNullOrWhiteSpace(token))
        {
            // An inserted or updated row still exists at undo time, so its inverse
            // must guard against the stored post-write version. A deleted row has no
            // target to guard; its restore carries the captured pre-delete version.
            var tokenImage = delta.Op switch
            {
                "insert" or "update" => DeserializeObject(delta.AfterImage, "post-write image"),
                "delete" => data,
                _ => throw new BifrostExecutionError("The deferred delta has an invalid operation."),
            };
            if (!tokenImage.TryGetValue(token, out var version))
                throw new BifrostExecutionError("The deferred delta is missing its captured concurrency token.");
            data[token] = version;
        }

        return new MutationIntent
        {
            Table = table.DbName,
            Action = delta.InverseOp switch
            {
                "delete" => MutationIntentAction.Delete,
                "restore" => MutationIntentAction.Restore,
                _ => throw new BifrostExecutionError("The deferred delta has an invalid inverse operation."),
            },
            RestoreSoftDeleted = restoringSoftDeletedRow,
            Data = data,
            PrimaryKey = delta.InverseOp == "restore" && !restoringSoftDeletedRow
                ? null
                : table.KeyColumns.Select(column =>
                    key.TryGetValue(column.ColumnName, out var value)
                        ? value
                        : throw new BifrostExecutionError("The deferred delta is missing a primary-key value.")).ToArray(),
            UserContext = userContext,
        };
    }

    private IDbTable FindTable(string name) => _model.Tables.FirstOrDefault(table =>
        string.Equals($"{table.TableSchema}.{table.DbName}", name, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(table.DbName, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new BifrostExecutionError("The deferred delta target table is unavailable.");

    private async Task<ChangeSet> LoadChangeSetAsync(long id, CancellationToken cancellationToken)
    {
        var table = FindTable(MetadataKeys.Deferred.ChangeSet.Table);
        await using var connection = _connections.GetConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {_connections.Dialect.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.State)}, {_connections.Dialect.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.UndoWindowExpiresAt)} FROM {_connections.Dialect.TableReference(table.TableSchema, table.DbName)} WHERE {_connections.Dialect.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.Id)} = @id";
        Add(command, "@id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new BifrostExecutionError("The deferred change set was not found.");
        return new ChangeSet(reader.GetString(0), DateTimeOffset.Parse(reader.GetValue(1).ToString()!));
    }

    private async Task<IReadOnlyList<Delta>> LoadDeltasAsync(long changeSetId, CancellationToken cancellationToken)
    {
        var table = FindTable(MetadataKeys.Deferred.ChangeSetDelta.Table);
        var rows = new List<Delta>();
        await using var connection = _connections.GetConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {_connections.Dialect.EscapeIdentifier(MetadataKeys.Deferred.ChangeSetDelta.Column.Table)}, {_connections.Dialect.EscapeIdentifier(MetadataKeys.Deferred.ChangeSetDelta.Column.Pk)}, {_connections.Dialect.EscapeIdentifier(MetadataKeys.Deferred.ChangeSetDelta.Column.Op)}, {_connections.Dialect.EscapeIdentifier(MetadataKeys.Deferred.ChangeSetDelta.Column.InverseOp)}, {_connections.Dialect.EscapeIdentifier(MetadataKeys.Deferred.ChangeSetDelta.Column.BeforeImage)}, {_connections.Dialect.EscapeIdentifier(MetadataKeys.Deferred.ChangeSetDelta.Column.AfterImage)} FROM {_connections.Dialect.TableReference(table.TableSchema, table.DbName)} WHERE {_connections.Dialect.EscapeIdentifier(MetadataKeys.Deferred.ChangeSetDelta.Column.ChangeSetId)} = @id ORDER BY {_connections.Dialect.EscapeIdentifier("id")}";
        Add(command, "@id", changeSetId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new Delta(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        return rows;
    }

    private async Task SetStateAsync(long id, string state, CancellationToken cancellationToken)
    {
        var table = FindTable(MetadataKeys.Deferred.ChangeSet.Table);
        await using var connection = _connections.GetConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE {_connections.Dialect.TableReference(table.TableSchema, table.DbName)} SET {_connections.Dialect.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.State)} = @state, {_connections.Dialect.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.ReversedAt)} = @at WHERE {_connections.Dialect.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.Id)} = @id AND {_connections.Dialect.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.State)}=@undoing";
        Add(command, "@state", state); Add(command, "@at", DateTimeOffset.UtcNow); Add(command, "@id", id); Add(command, "@undoing", Undoing);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new BifrostExecutionError("The deferred change set state could not be recorded.");
    }

    private async Task<bool> TryClaimUndoAsync(long id, string expectedState, CancellationToken cancellationToken)
    {
        var table = FindTable(MetadataKeys.Deferred.ChangeSet.Table);
        await using var connection = _connections.GetConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var expiryGuard = string.Equals(expectedState, Held, StringComparison.OrdinalIgnoreCase)
            ? $" AND {_connections.Dialect.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.UndoWindowExpiresAt)}>@now"
            : string.Empty;
        command.CommandText = $"UPDATE {_connections.Dialect.TableReference(table.TableSchema, table.DbName)} SET {_connections.Dialect.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.State)}=@claim WHERE {_connections.Dialect.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.Id)}=@id AND {_connections.Dialect.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.State)}=@expected{expiryGuard}";
        Add(command, "@claim", Undoing); Add(command, "@id", id); Add(command, "@expected", expectedState);
        if (string.Equals(expectedState, Held, StringComparison.OrdinalIgnoreCase)) Add(command, "@now", _clock());
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private async Task<bool> InverseAlreadyAppliedAsync(Delta delta, CancellationToken cancellationToken)
    {
        var table = FindTable(delta.Table);
        var key = DeserializeObject(delta.Pk, "primary key");
        var d = _connections.Dialect;
        await using var connection = _connections.GetConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var predicates = new List<string>();
        foreach (var column in table.KeyColumns)
        {
            predicates.Add($"{d.EscapeIdentifier(column.ColumnName)}=@{column.ColumnName}");
            Add(command, "@" + column.ColumnName, key[column.ColumnName]);
        }
        command.CommandText = $"SELECT * FROM {d.TableReference(table.TableSchema, table.DbName)} WHERE {string.Join(" AND ", predicates)}";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return string.Equals(delta.InverseOp, "delete", StringComparison.Ordinal);
        if (!string.Equals(delta.InverseOp, "restore", StringComparison.Ordinal))
            return false;
        var desired = DeserializeObject(delta.BeforeImage, "before image");
        var token = table.GetMetadataValue(MetadataKeys.Concurrency.Token);
        foreach (var pair in desired)
        {
            if (string.Equals(pair.Key, token, StringComparison.OrdinalIgnoreCase)) continue;
            var ordinal = reader.GetOrdinal(pair.Key);
            var actual = reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
            if (!string.Equals(Convert.ToString(actual), Convert.ToString(pair.Value), StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private async Task SettleHeldEventsAsync(long changeSetId, CancellationToken cancellationToken)
    {
        var outboxName = _model.GetMetadataValue(MetadataKeys.Cdc.OutboxTable);
        var outbox = string.IsNullOrWhiteSpace(outboxName) ? null : ModelTableReference.Find(_model, outboxName);
        if (outbox is null || !outbox.Columns.Any(c => string.Equals(c.ColumnName, DeferredOutboxColumns.State, StringComparison.OrdinalIgnoreCase)))
            return;
        var d = _connections.Dialect;
        var table = d.TableReference(outbox.TableSchema, outbox.DbName);
        await using var connection = _connections.GetConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var columns = new[] { MetadataKeys.Cdc.ColAggregate, MetadataKeys.Cdc.ColOp, MetadataKeys.Cdc.ColPayload, MetadataKeys.Cdc.ColTenant, MetadataKeys.Cdc.ColCreatedAt, MetadataKeys.Cdc.ColAttempts, MetadataKeys.Cdc.ColDead, DeferredOutboxColumns.State, DeferredOutboxColumns.ChangeSetId };
        await using (var compensate = connection.CreateCommand())
        {
            compensate.Transaction = transaction;
            compensate.CommandText = $"INSERT INTO {table} ({string.Join(",", columns.Select(d.EscapeIdentifier))}) SELECT {d.EscapeIdentifier(MetadataKeys.Cdc.ColAggregate)},@op,{d.EscapeIdentifier(MetadataKeys.Cdc.ColPayload)},{d.EscapeIdentifier(MetadataKeys.Cdc.ColTenant)},@now,0,@dead,@pending,{d.EscapeIdentifier(DeferredOutboxColumns.ChangeSetId)} FROM {table} WHERE {d.EscapeIdentifier(DeferredOutboxColumns.ChangeSetId)}=@id AND {d.EscapeIdentifier(MetadataKeys.Cdc.ColDispatchedAt)} IS NOT NULL AND NOT EXISTS (SELECT 1 FROM {table} WHERE {d.EscapeIdentifier(DeferredOutboxColumns.ChangeSetId)}=@id AND {d.EscapeIdentifier(MetadataKeys.Cdc.ColOp)}=@op)";
            Add(compensate, "@op", "compensate"); Add(compensate, "@now", DateTimeOffset.UtcNow); Add(compensate, "@dead", false); Add(compensate, "@pending", DeferredOutboxColumns.Pending); Add(compensate, "@id", changeSetId);
            await compensate.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var suppress = connection.CreateCommand())
        {
            suppress.Transaction = transaction;
            suppress.CommandText = $"UPDATE {table} SET {d.EscapeIdentifier(DeferredOutboxColumns.State)}=@suppressed WHERE {d.EscapeIdentifier(DeferredOutboxColumns.ChangeSetId)}=@id AND {d.EscapeIdentifier(MetadataKeys.Cdc.ColDispatchedAt)} IS NULL AND {d.EscapeIdentifier(MetadataKeys.Cdc.ColOp)}<>@compensate";
            Add(suppress, "@suppressed", DeferredOutboxColumns.Suppressed); Add(suppress, "@id", changeSetId); Add(suppress, "@compensate", "compensate");
            await suppress.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static Dictionary<string, object?> DeserializeObject(string? json, string name)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new BifrostExecutionError($"The deferred delta is missing its {name}.");
        var document = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
            ?? throw new BifrostExecutionError($"The deferred delta has an invalid {name}.");
        return document.ToDictionary(pair => pair.Key, pair => ToValue(pair.Value), StringComparer.OrdinalIgnoreCase);
    }

    private static object? ToValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null, JsonValueKind.String => value.GetString(), JsonValueKind.True => true, JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer, JsonValueKind.Number => value.GetDecimal(),
        _ => throw new BifrostExecutionError("The deferred delta contains an unsupported value."),
    };
    private static void Add(DbCommand command, string name, object? value) { var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value ?? DBNull.Value; command.Parameters.Add(parameter); }
    private sealed record ChangeSet(string State, DateTimeOffset ExpiresAt);
    private sealed record Delta(string Table, string Pk, string Op, string InverseOp, string? BeforeImage, string? AfterImage);
}

public sealed record DeferredUndoResult(long ChangeSetId, int UndoneRows, int ConflictRows, bool AlreadyUndone);
