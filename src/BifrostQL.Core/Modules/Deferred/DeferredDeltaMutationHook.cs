using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.History;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using static BifrostQL.Core.Resolvers.DbParameterBinder;

namespace BifrostQL.Core.Modules.Deferred;

/// <summary>
/// Writes the durable undo contract immediately after a deferrable mutation. Every delta is
/// bound to one typed active held change set and records the full stored before-image for a
/// restore inverse. Insert/update deltas record the full stored post-image, including database
/// generated concurrency tokens, so a later delete/restore predicate can reject a newer write;
/// delete deltas retain the pre-delete token in their before-image. The delta and its held change
/// set use the mutation's connection, so either both commit with the write or a hook failure
/// rolls all three back.
/// </summary>
public sealed class DeferredDeltaMutationHook : IInTransactionMutationHook
{
    internal const string ActiveChangeSetKey = "bifrost.deferred.active-change-set";
    private const string HeldLifecycle = "held";
    private const string HookSource = "deferred-delta";

    // A state bag may be shared by a batch or TreeSync transaction. The value is deliberately
    // typed and bound to that exact transaction seam; an id alone could be replayed into a
    // later mutation and attach its delta to the wrong held change set.
    private sealed record ActiveChangeSet(
        object Id,
        string Lifecycle,
        string Source,
        DbConnection Connection,
        DbTransaction? Transaction,
        object TransactionMarker,
        IDbModel Model,
        IDbTable ChangeSetTable,
        IDbTable DeltaTable,
        HashSet<string> TouchedTables);

    public async ValueTask AfterWriteInTransactionAsync(MutationObserverContext context)
    {
        var config = DeferredConfig.FromTable(context.Table);
        if (!config.IsDeferrable)
            return;

        if (IsUpdateOrDelete(context.MutationType) && AffectedZeroRows(context.Result))
            return;

        if (context.Connection is null || context.Model is null || context.Dialect is null)
            throw new BifrostExecutionError(
                "Deferred delta writer was invoked without an open mutation transaction.");

        var capturedBefore = context.MutationType == MutationType.Insert
            ? null
            : GetRequiredCapturedBeforeImage(context);
        var operation = context.MutationType == MutationType.Update && capturedBefore is null
            ? MutationType.Insert
            : context.MutationType;
        if (operation != MutationType.Insert && capturedBefore is null)
            throw new BifrostExecutionError(
                $"No before-image exists for deferrable {context.MutationType.ToString().ToLowerInvariant()} of " +
                $"'{context.Table.TableSchema}.{context.Table.DbName}'. Refusing to create an empty reverse delta.");

        var activeChangeSet = await GetOrCreateChangeSetAsync(context, config);
        var key = ResolveKeyData(context.Table, context.Data, context.Result, capturedBefore, operation);
        IReadOnlyDictionary<string, object?>? storedAfter = null;
        if (operation != MutationType.Delete)
        {
            storedAfter = await MutationCommandExecutor.LoadRowByKey(
                context.Connection, context.Transaction, context.Dialect, context.Table,
                context.Table.Columns.Select(column => column.ColumnName).ToList(), key);
            if (storedAfter is null)
                throw new BifrostExecutionError(
                    $"Deferred delta could not read back the row it just wrote in " +
                    $"'{context.Table.TableSchema}.{context.Table.DbName}'; refusing to record an unsafe undo contract.");
        }

        var deltaTable = activeChangeSet.DeltaTable;
        var delta = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [MetadataKeys.Deferred.ChangeSetDelta.Column.ChangeSetId] = activeChangeSet.Id,
            [MetadataKeys.Deferred.ChangeSetDelta.Column.Table] = $"{context.Table.TableSchema}.{context.Table.DbName}",
            [MetadataKeys.Deferred.ChangeSetDelta.Column.Pk] = JsonSerializer.Serialize(key),
            [MetadataKeys.Deferred.ChangeSetDelta.Column.Op] = operation.ToString().ToLowerInvariant(),
            [MetadataKeys.Deferred.ChangeSetDelta.Column.InverseOp] = InverseOperation(operation),
            [MetadataKeys.Deferred.ChangeSetDelta.Column.BeforeImage] = capturedBefore is null ? null : JsonSerializer.Serialize(capturedBefore),
            // The inverse predicate must compare against the row exactly as the database stored
            // it, not the write input: defaults, triggers, and generated concurrency tokens are
            // otherwise absent or stale. Deletes retain their pre-delete token in before_image.
            [MetadataKeys.Deferred.ChangeSetDelta.Column.AfterImage] = storedAfter is null ? null : JsonSerializer.Serialize(storedAfter),
            [MetadataKeys.Deferred.ChangeSetDelta.Column.CreatedAt] = DateTime.UtcNow,
        };

        var tableRef = context.Dialect.TableReference(deltaTable.TableSchema, deltaTable.DbName);
        var sql = MutationCommandExecutor.BuildInsertInto(context.Dialect, deltaTable, tableRef, delta.Keys) + ";";
        await MutationCommandExecutor.ExecuteNonQuery(context.Connection, context.Transaction, sql, delta);
    }

    private static async ValueTask<ActiveChangeSet> GetOrCreateChangeSetAsync(MutationObserverContext context, DeferredConfig config)
    {
        var changeSetTable = RequireTable(context.Model!, MetadataKeys.Deferred.ChangeSet.Table);
        var deltaTable = RequireTable(context.Model!, MetadataKeys.Deferred.ChangeSetDelta.Table);
        if (context.MutationState.TryGetValue(ActiveChangeSetKey, out var stored))
        {
            if (stored is not ActiveChangeSet active)
                throw new BifrostExecutionError("Deferred change-set mutation state has an invalid type.");

            ValidateActiveChangeSet(active, context, changeSetTable, deltaTable);
            var touchedTableName = $"{context.Table.TableSchema}.{context.Table.DbName}";
            if (active.TouchedTables.Add(touchedTableName))
            {
                var changeSetTableRef = context.Dialect!.TableReference(changeSetTable.TableSchema, changeSetTable.DbName);
                var update = new Dictionary<string, object?>
                {
                    [MetadataKeys.Deferred.ChangeSet.Column.Tables] = JsonSerializer.Serialize(active.TouchedTables),
                    [MetadataKeys.Deferred.ChangeSet.Column.Id] = active.Id,
                };
                var sql = $"UPDATE {changeSetTableRef} SET {context.Dialect.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.Tables)}=" +
                          $"@{MetadataKeys.Deferred.ChangeSet.Column.Tables} WHERE {context.Dialect.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.Id)}=@{MetadataKeys.Deferred.ChangeSet.Column.Id};";
                await MutationCommandExecutor.ExecuteNonQuery(context.Connection!, context.Transaction, sql, update);
            }
            return active;
        }

        var tenantKey = context.Model!.GetMetadataValue(MetadataKeys.Security.TenantContextKey)
            ?? MetadataKeys.Auth.DefaultTenantContextKey;
        context.UserContext.TryGetValue(tenantKey, out var tenant);
        var tableName = $"{context.Table.TableSchema}.{context.Table.DbName}";
        var changeSet = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [MetadataKeys.Deferred.ChangeSet.Column.State] = HeldLifecycle,
            [MetadataKeys.Deferred.ChangeSet.Column.UndoWindowExpiresAt] = DateTime.UtcNow.Add(config.UndoWindow!.Value),
            [MetadataKeys.Deferred.ChangeSet.Column.Requester] = AuditMutationTransformer.ResolveActor(context.Model!, context.UserContext)?.ToString(),
            [MetadataKeys.Deferred.ChangeSet.Column.Tenant] = tenant?.ToString(),
            [MetadataKeys.Deferred.ChangeSet.Column.Tables] = JsonSerializer.Serialize(new[] { tableName }),
            [MetadataKeys.Deferred.ChangeSet.Column.CreatedAt] = DateTime.UtcNow,
        };

        var tableRef = context.Dialect!.TableReference(changeSetTable.TableSchema, changeSetTable.DbName);
        var insert = MutationCommandExecutor.BuildInsertInto(context.Dialect, changeSetTable, tableRef, changeSet.Keys);
        var returning = context.Dialect.ReturningIdentityClauseFor(changeSetTable.KeyColumns.Select(c => c.ColumnName).ToList());
        var id = returning is null
            ? await InsertAndReadIdentityAsync(context.Connection!, context.Transaction, insert + ";", context.Dialect.LastInsertedIdentity, changeSet)
            : await ExecuteScalarAsync(context.Connection!, context.Transaction, insert + returning + ";", changeSet);
        if (id is null || id is DBNull)
            throw new BifrostExecutionError("Deferred change-set store did not return its generated identity.");

        var created = new ActiveChangeSet(
            id, HeldLifecycle, HookSource, context.Connection!, context.Transaction,
            (object?)context.Transaction ?? context.Connection!, context.Model!, changeSetTable, deltaTable,
            new HashSet<string>(StringComparer.Ordinal) { tableName });
        context.MutationState[ActiveChangeSetKey] = created;
        return created;
    }

    private static void ValidateActiveChangeSet(
        ActiveChangeSet active, MutationObserverContext context, IDbTable changeSetTable, IDbTable deltaTable)
    {
        if (active.Id is null
            || active.Lifecycle != HeldLifecycle
            || active.Source != HookSource
            || !ReferenceEquals(active.Connection, context.Connection)
            || !ReferenceEquals(active.Transaction, context.Transaction)
            || !ReferenceEquals(active.TransactionMarker, (object?)context.Transaction ?? context.Connection)
            || !ReferenceEquals(active.Model, context.Model)
            || !SameTable(active.ChangeSetTable, changeSetTable)
            || !SameTable(active.DeltaTable, deltaTable))
            throw new BifrostExecutionError(
                "Deferred change-set mutation state does not belong to this model transaction.");
    }

    private static bool SameTable(IDbTable left, IDbTable right)
        => string.Equals(left.TableSchema, right.TableSchema, StringComparison.OrdinalIgnoreCase)
           && string.Equals(left.DbName, right.DbName, StringComparison.OrdinalIgnoreCase);

    private static async ValueTask<object?> InsertAndReadIdentityAsync(
        DbConnection connection, DbTransaction? transaction, string insert, string identity, Dictionary<string, object?> data)
    {
        await MutationCommandExecutor.ExecuteNonQuery(connection, transaction, insert, data);
        return await ExecuteScalarAsync(connection, transaction, $"SELECT {identity};", new Dictionary<string, object?>());
    }

    private static async ValueTask<object?> ExecuteScalarAsync(
        DbConnection connection, DbTransaction? transaction, string sql, Dictionary<string, object?> data)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        AddParameters(command, data);
        return await command.ExecuteScalarAsync();
    }

    private static IDbTable RequireTable(IDbModel model, string name)
        => ModelTableReference.Find(model, name)
           ?? throw new BifrostExecutionError($"Deferred store table '{name}' was not found in the model.");

    private static IReadOnlyDictionary<string, object?>? GetRequiredCapturedBeforeImage(MutationObserverContext context)
    {
        if (!context.MutationState.TryGetValue(HistoryMutationHook.BeforeImageKey, out var captured)
            || captured is not HistoryMutationHook.BeforeImage image)
            throw new BifrostExecutionError(
                $"No before-image was captured for deferrable {context.MutationType.ToString().ToLowerInvariant()} of " +
                $"'{context.Table.TableSchema}.{context.Table.DbName}'. Refusing to create a reverse delta.");
        return image.Row;
    }

    private static Dictionary<string, object?> ResolveKeyData(
        IDbTable table, IDictionary<string, object?> data, object? result,
        IReadOnlyDictionary<string, object?>? before, MutationType operation)
    {
        var resultKey = result as IReadOnlyDictionary<string, object?>;
        var key = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in table.KeyColumns)
        {
            if (operation == MutationType.Delete
                && resultKey?.TryGetValue(column.ColumnName, out var resultValue) == true
                && resultValue is not null)
                key[column.ColumnName] = resultValue;
            else if (data.TryGetValue(column.ColumnName, out var value) && value is not null)
                key[column.ColumnName] = value;
            else if (before is not null && before.TryGetValue(column.ColumnName, out value) && value is not null)
                key[column.ColumnName] = value;
            else if (table.KeyColumns.Count() == 1 && operation == MutationType.Insert && result is not null)
                key[column.ColumnName] = result;
            else
                throw new BifrostExecutionError(
                    $"Deferred delta cannot resolve primary-key column '{column.ColumnName}' of " +
                    $"'{table.TableSchema}.{table.DbName}'.");
        }
        return key;
    }

    private static string InverseOperation(MutationType operation)
        => operation == MutationType.Insert ? "delete" : "restore";

    private static bool IsUpdateOrDelete(MutationType mutationType)
        => mutationType is MutationType.Update or MutationType.Delete;

    private static bool AffectedZeroRows(object? result)
        => result is not null
           && (result is int i ? i == 0
               : result is long l ? l == 0
               : result is IConvertible c && Convert.ToInt64(c) == 0);
}
