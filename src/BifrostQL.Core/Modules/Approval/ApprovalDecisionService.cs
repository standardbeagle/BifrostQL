using System.Data.Common;
using System.Text.Json;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.Modules.Approval;

/// <summary>Applies or rejects rows in the approval queue.</summary>
public sealed class ApprovalDecisionService
{
    private readonly IDbModel _model;
    private readonly IDbConnFactory _connections;
    private readonly IMutationIntentExecutor _mutations;
    private readonly PolicyEvaluator _policy;

    public ApprovalDecisionService(
        IDbModel model,
        IDbConnFactory connections,
        IMutationIntentExecutor mutations,
        PolicyEvaluator? policy = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _mutations = mutations ?? throw new ArgumentNullException(nameof(mutations));
        _policy = policy ?? new PolicyEvaluator();
    }

    public async Task ApproveAsync(long pendingChangeId, IDictionary<string, object?> approverContext,
        CancellationToken cancellationToken = default)
    {
        var row = await LoadAsync(pendingChangeId, cancellationToken);
        Authorize(row, approverContext);

        var target = FindTable(row.Table);
        // Replay under the requester identity so tenant, policy, and audit transformers see
        // the same caller that submitted the change; approver identity is carried only to stamp
        // the approval decision. This prevents approver privileges from widening the replay.
        var approver = PolicyIdentity.FromUserContext(approverContext).Id;
        var requesterContext = DeserializeContext(row.RequesterContext);
        requesterContext[AuditMutationTransformer.ActorOverrideKey] =
            AuditMutationTransformer.CreateActorOverride(approver);
        ApprovalInterceptMutationHook.MarkApprovedReplay(requesterContext, pendingChangeId, approver);

        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.Payload)
            ?? throw new BifrostExecutionError("Approval payload is invalid.");
        var values = payload.ToDictionary(kv => kv.Key, kv => ToValue(kv.Value), StringComparer.OrdinalIgnoreCase);
        var action = ParseAction(row.Op);
        IReadOnlyList<object?>? key = null;
        if (action != MutationIntentAction.Insert)
        {
            var keyValues = target.KeyColumns.Select(column =>
                values.TryGetValue(column.ColumnName, out var value)
                    ? value
                    : throw new BifrostExecutionError("Approval payload does not contain the target primary key.")).ToArray();
            key = keyValues;
            foreach (var column in target.KeyColumns)
                values.Remove(column.ColumnName);

            // A queued soft-delete payload contains the physical UPDATE's stamped columns.
            // Replay its logical DELETE with only its predicate: the normal mutation pipeline
            // re-applies soft-delete, tenant, policy, audit, and encryption transformers.
            if (action == MutationIntentAction.Delete)
                values.Clear();
        }

        // The replay applies the data write AND (via the intercept hook's before-commit phase)
        // stamps the pending -> approved transition inside ONE transaction. If either fails the
        // whole thing rolls back, so a decision is never half-applied.
        await _mutations.ExecuteAsync(new MutationIntent
        {
            Table = target.DbName,
            Action = action,
            Data = values,
            PrimaryKey = key,
            UserContext = requesterContext,
        }, cancellationToken);
    }

    public async Task RejectAsync(long pendingChangeId, string reason,
        IDictionary<string, object?> approverContext, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A rejection reason is required.", nameof(reason));
        var row = await LoadAsync(pendingChangeId, cancellationToken);
        Authorize(row, approverContext);
        await TransitionAsync(pendingChangeId, PendingChangeStore.StateRejected,
            PolicyIdentity.FromUserContext(approverContext).Id, reason, cancellationToken);
    }

    private void Authorize(PendingRow row, IDictionary<string, object?> context)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (!string.Equals(row.State, PendingChangeStore.StatePending, StringComparison.OrdinalIgnoreCase))
            throw new BifrostExecutionError("The pending change has already been decided.");

        var table = FindTable(row.Table);
        var config = ApprovalConfig.FromTable(table);
        var identity = PolicyIdentity.FromUserContext(context);
        var selfApproval = string.Equals(row.Requester, identity.Id, StringComparison.Ordinal);
        // Keep this comparison unconditional: it is the four-eyes decision, not an optional
        // branch hidden behind requester existence or another authorization result.
        if (!config.SelfApprove && selfApproval)
            throw new BifrostExecutionError("The requester cannot approve their own change.");
        if (!identity.Roles.Any(role => string.Equals(role, config.ApproverRole, StringComparison.OrdinalIgnoreCase)))
            throw new BifrostExecutionError("The caller is not an approval-role holder.");
        try
        {
            if (_policy.CanAct(PolicyConfigCollector.FromTable(table), PolicyActionFor(row.Op), identity) != PolicyDecision.Allow)
                throw new BifrostExecutionError("The caller is not authorized to approve this change.");
        }
        catch (InvalidOperationException)
        {
            throw new BifrostExecutionError("The approval policy could not be evaluated.");
        }
    }

    private IDbTable FindTable(string qualifiedName)
    {
        var table = _model.Tables.FirstOrDefault(candidate =>
            string.Equals($"{candidate.TableSchema}.{candidate.DbName}", qualifiedName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.DbName, qualifiedName, StringComparison.OrdinalIgnoreCase));
        return table ?? throw new BifrostExecutionError("The approval target table is unavailable.");
    }

    private async Task<PendingRow> LoadAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = _connections.GetConnection();
        await connection.OpenAsync(cancellationToken);
        var table = _model.Tables.FirstOrDefault(t =>
            string.Equals(t.DbName, PendingChangeStore.TableName, StringComparison.OrdinalIgnoreCase));
        if (table is null) throw new BifrostExecutionError("The pending-changes store is unavailable.");
        var dialect = _connections.Dialect;
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {string.Join(", ", PendingChangeStore.Columns.Skip(1).Select(dialect.EscapeIdentifier))} FROM {dialect.TableReference(table.TableSchema, table.DbName)} WHERE {dialect.EscapeIdentifier(PendingChangeStore.ColId)} = @id";
        Add(command, "@id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new BifrostExecutionError("The pending change was not found.");
        return new PendingRow(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetValue(4)?.ToString(),
            reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6),
            reader.IsDBNull(9) ? null : reader.GetValue(9)?.ToString());
    }

    private async Task TransitionAsync(long id, string state, string approver, string? reason, CancellationToken cancellationToken)
    {
        await using var connection = _connections.GetConnection();
        await connection.OpenAsync(cancellationToken);
        var table = _model.Tables.First(t => string.Equals(t.DbName, PendingChangeStore.TableName, StringComparison.OrdinalIgnoreCase));
        var d = _connections.Dialect;
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE {d.TableReference(table.TableSchema, table.DbName)} SET {d.EscapeIdentifier(PendingChangeStore.ColState)} = @state, {d.EscapeIdentifier(PendingChangeStore.ColApprover)} = @approver, {d.EscapeIdentifier(PendingChangeStore.ColDecidedAt)} = @decided, {d.EscapeIdentifier(PendingChangeStore.ColReason)} = @reason WHERE {d.EscapeIdentifier(PendingChangeStore.ColId)} = @id AND {d.EscapeIdentifier(PendingChangeStore.ColState)} = @pending";
        Add(command, "@state", state); Add(command, "@approver", approver); Add(command, "@decided", DateTimeOffset.UtcNow); Add(command, "@reason", reason); Add(command, "@id", id); Add(command, "@pending", PendingChangeStore.StatePending);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new BifrostExecutionError("The pending change could not be transitioned.");
    }

    private static PolicyAction PolicyActionFor(string op) => op.ToLowerInvariant() switch
    {
        "insert" => PolicyAction.Create,
        "update" => PolicyAction.Update,
        "delete" => PolicyAction.Delete,
        _ => throw new BifrostExecutionError("The pending change operation is invalid."),
    };

    private static MutationIntentAction ParseAction(string op) => op.ToLowerInvariant() switch
    {
        "insert" => MutationIntentAction.Insert,
        "update" => MutationIntentAction.Update,
        "delete" => MutationIntentAction.Delete,
        _ => throw new BifrostExecutionError("The pending change operation is invalid."),
    };

    private static IDictionary<string, object?> DeserializeContext(string? serialized)
    {
        if (string.IsNullOrWhiteSpace(serialized))
            throw new BifrostExecutionError("The pending change is missing its requester identity.");

        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(serialized)
            ?? throw new BifrostExecutionError("The pending change requester identity is invalid.");
        return values.ToDictionary(pair => pair.Key, pair => ToValue(pair.Value), StringComparer.OrdinalIgnoreCase);
    }

    private static object? ToValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => value.GetDecimal(),
        JsonValueKind.Array => value.EnumerateArray().Select(ToValue).ToArray(),
        JsonValueKind.Object => value.EnumerateObject().ToDictionary(pair => pair.Name, pair => ToValue(pair.Value), StringComparer.OrdinalIgnoreCase),
        _ => throw new BifrostExecutionError("The pending change requester identity contains an unsupported value."),
    };

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value ?? DBNull.Value; command.Parameters.Add(parameter);
    }

    private sealed record PendingRow(
        string Table, string Op, string Payload, string Requester, string? Tenant,
        string? RequesterContext, string State, string? Reason);
}
