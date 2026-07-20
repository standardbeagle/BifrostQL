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
/// Writes a durable reverse delta immediately after a deferrable mutation. The delta and
/// its held change set use the mutation's connection, so either both commit with the write
/// or a hook failure rolls all three back.
/// </summary>
public sealed class DeferredDeltaMutationHook : IInTransactionMutationHook
{
    internal const string ChangeSetIdKey = "bifrost.deferred.change-set-id";
    private const string TouchedTablesKey = "bifrost.deferred.change-set-tables";

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

        var before = context.MutationType == MutationType.Insert
            ? null
            : HistoryMutationHook.GetCapturedBeforeImage(context)
                ?? throw new BifrostExecutionError(
                    $"No before-image exists for deferrable {context.MutationType.ToString().ToLowerInvariant()} of " +
                    $"'{context.Table.TableSchema}.{context.Table.DbName}'. Refusing to create an empty reverse delta.");

        var changeSetId = await GetOrCreateChangeSetAsync(context, config);
        var key = ResolveKeyData(context.Table, context.Data, context.Result, before);
        var deltaTable = RequireTable(context.Model, MetadataKeys.Deferred.ChangeSetDelta.Table);
        var delta = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [MetadataKeys.Deferred.ChangeSetDelta.Column.ChangeSetId] = changeSetId,
            [MetadataKeys.Deferred.ChangeSetDelta.Column.Table] = $"{context.Table.TableSchema}.{context.Table.DbName}",
            [MetadataKeys.Deferred.ChangeSetDelta.Column.Pk] = JsonSerializer.Serialize(key),
            [MetadataKeys.Deferred.ChangeSetDelta.Column.Op] = context.MutationType.ToString().ToLowerInvariant(),
            [MetadataKeys.Deferred.ChangeSetDelta.Column.InverseOp] = InverseOperation(context.MutationType, before),
            [MetadataKeys.Deferred.ChangeSetDelta.Column.BeforeImage] = before is null ? null : JsonSerializer.Serialize(before),
            [MetadataKeys.Deferred.ChangeSetDelta.Column.AfterImage] = null,
            [MetadataKeys.Deferred.ChangeSetDelta.Column.CreatedAt] = DateTime.UtcNow,
        };

        var tableRef = context.Dialect.TableReference(deltaTable.TableSchema, deltaTable.DbName);
        var sql = MutationCommandExecutor.BuildInsertInto(context.Dialect, deltaTable, tableRef, delta.Keys) + ";";
        await MutationCommandExecutor.ExecuteNonQuery(context.Connection, context.Transaction, sql, delta);
    }

    private static async ValueTask<object> GetOrCreateChangeSetAsync(MutationObserverContext context, DeferredConfig config)
    {
        var changeSetTable = RequireTable(context.Model!, MetadataKeys.Deferred.ChangeSet.Table);
        if (context.MutationState.TryGetValue(ChangeSetIdKey, out var existing) && existing is not null)
        {
            var tables = (HashSet<string>)context.MutationState[TouchedTablesKey]!;
            var tableName = $"{context.Table.TableSchema}.{context.Table.DbName}";
            if (tables.Add(tableName))
            {
                var changeSetTableRef = context.Dialect!.TableReference(changeSetTable.TableSchema, changeSetTable.DbName);
                var update = new Dictionary<string, object?>
                {
                    [MetadataKeys.Deferred.ChangeSet.Column.Tables] = JsonSerializer.Serialize(tables),
                    [MetadataKeys.Deferred.ChangeSet.Column.Id] = existing,
                };
                var sql = $"UPDATE {changeSetTableRef} SET {context.Dialect.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.Tables)}=" +
                          $"@{MetadataKeys.Deferred.ChangeSet.Column.Tables} WHERE {context.Dialect.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.Id)}=@{MetadataKeys.Deferred.ChangeSet.Column.Id};";
                await MutationCommandExecutor.ExecuteNonQuery(context.Connection!, context.Transaction, sql, update);
            }
            return existing;
        }

        var tenantKey = context.Model!.GetMetadataValue(MetadataKeys.Security.TenantContextKey)
            ?? MetadataKeys.Auth.DefaultTenantContextKey;
        context.UserContext.TryGetValue(tenantKey, out var tenant);
        var changeSet = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [MetadataKeys.Deferred.ChangeSet.Column.State] = "held",
            [MetadataKeys.Deferred.ChangeSet.Column.UndoWindowExpiresAt] = DateTime.UtcNow.Add(config.UndoWindow!.Value),
            [MetadataKeys.Deferred.ChangeSet.Column.Requester] = AuditMutationTransformer.ResolveActor(context.Model!, context.UserContext)?.ToString(),
            [MetadataKeys.Deferred.ChangeSet.Column.Tenant] = tenant?.ToString(),
            [MetadataKeys.Deferred.ChangeSet.Column.Tables] = JsonSerializer.Serialize(new[] { $"{context.Table.TableSchema}.{context.Table.DbName}" }),
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

        context.MutationState[ChangeSetIdKey] = id;
        context.MutationState[TouchedTablesKey] = new HashSet<string>(StringComparer.Ordinal)
        {
            $"{context.Table.TableSchema}.{context.Table.DbName}",
        };
        return id;
    }

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

    private static Dictionary<string, object?> ResolveKeyData(
        IDbTable table, IDictionary<string, object?> data, object? result,
        IReadOnlyDictionary<string, object?>? before)
    {
        var key = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in table.KeyColumns)
        {
            if (data.TryGetValue(column.ColumnName, out var value) && value is not null)
                key[column.ColumnName] = value;
            else if (before is not null && before.TryGetValue(column.ColumnName, out value) && value is not null)
                key[column.ColumnName] = value;
            else if (table.KeyColumns.Count() == 1 && result is not null)
                key[column.ColumnName] = result;
            else
                throw new BifrostExecutionError(
                    $"Deferred delta cannot resolve primary-key column '{column.ColumnName}' of " +
                    $"'{table.TableSchema}.{table.DbName}'.");
        }
        return key;
    }

    private static string InverseOperation(MutationType mutationType, IReadOnlyDictionary<string, object?>? before)
        => mutationType == MutationType.Insert || (mutationType == MutationType.Update && before is null)
            ? "delete"
            : "restore";

    private static bool IsUpdateOrDelete(MutationType mutationType)
        => mutationType is MutationType.Update or MutationType.Delete;

    private static bool AffectedZeroRows(object? result)
        => result is not null
           && (result is int i ? i == 0
               : result is long l ? l == 0
               : result is IConvertible c && Convert.ToInt64(c) == 0);
}
