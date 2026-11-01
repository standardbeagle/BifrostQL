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
        var row = await LoadAsync(pendingChangeId, approverContext, cancellationToken);
        Authorize(row, approverContext);

        var target = FindTable(row.Table);
        // Replay under the requester identity so tenant, policy, and audit transformers see
        // the same caller that submitted the change: the requester authored the data, so the
        // audit columns and the history actor name them. The approver is recorded only on the
        // pending change's decision (approver, decided_at) and never widens the replay.
        var approver = PolicyIdentity.FromUserContext(approverContext).Id;
        var requesterContext = DeserializeContext(row.RequesterContext);
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

    /// <summary>
    /// Marks a pending change expired. This is the seam for a scheduler/sweeper: it deliberately
    /// does not infer expiry from a timestamp at approval time, because the guarded
    /// pending-to-terminal transition is the authoritative decision. Trusted server code only:
    /// it takes no decider identity and runs no authorization.
    /// </summary>
    public async Task ExpireAsync(long pendingChangeId, string reason, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("An expiry reason is required.", nameof(reason));

        await TransitionAsync(pendingChangeId, PendingChangeStore.StateExpired, decider: null, reason, cancellationToken);
    }

    public async Task RejectAsync(long pendingChangeId, string reason,
        IDictionary<string, object?> approverContext, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A rejection reason is required.", nameof(reason));
        var row = await LoadAsync(pendingChangeId, approverContext, cancellationToken);
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

    /// <summary>
    /// Loads the pending change the decider may see. A change in another tenant answers
    /// exactly as a missing one, so sequential ids cannot be used to probe or decide other
    /// tenants' changes. The tenant check runs before every other check (state, self-approval,
    /// role, policy) so none of their answers leaks across tenants either.
    /// </summary>
    private async Task<PendingRow> LoadAsync(long id, IDictionary<string, object?> deciderContext,
        CancellationToken cancellationToken)
    {
        if (deciderContext is null) throw new ArgumentNullException(nameof(deciderContext));
        await using var connection = _connections.GetConnection();
        await connection.OpenAsync(cancellationToken);
        var table = RequireStore();
        var dialect = _connections.Dialect;
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {string.Join(", ", PendingChangeStore.Columns.Skip(1).Select(dialect.EscapeIdentifier))} FROM {dialect.TableReference(table.TableSchema, table.DbName)} WHERE {dialect.EscapeIdentifier(PendingChangeStore.ColId)} = @id";
        Add(command, "@id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw PendingChangeNotFound();
        var row = new PendingRow(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetValue(4)?.ToString(),
            reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6),
            reader.IsDBNull(9) ? null : reader.GetValue(9)?.ToString());
        if (!IsDecidableFromTenant(row, deciderContext))
            throw PendingChangeNotFound();
        return row;
    }

    private static BifrostExecutionError PendingChangeNotFound() => new("The pending change was not found.");

    /// <summary>
    /// A change is decidable only from inside its own tenant. The replay runs under the
    /// REQUESTER's identity, so tenant scoping downstream cannot catch an outsider decider:
    /// it has to be refused here. Two conditions, both under the model's tenant-context key:
    /// the row's <c>tenant</c> must equal the tenant of the identity it replays as (the gate
    /// writes both from one context, so a disagreement means the gate did not write the row),
    /// and the decider must hold that same tenant. A change with no tenant came from a
    /// requester with no tenant claim and is decidable without one.
    /// </summary>
    private bool IsDecidableFromTenant(PendingRow row, IDictionary<string, object?> deciderContext)
    {
        var tenantKey = TenantFilterTransformer.ResolveTenantContextKey(_model);
        var replayTenant = TenantOf(DeserializeContext(row.RequesterContext), tenantKey);
        if (!string.Equals(row.Tenant, replayTenant, StringComparison.Ordinal))
            return false;
        return row.Tenant is null
            || string.Equals(row.Tenant, TenantOf(deciderContext, tenantKey), StringComparison.Ordinal);
    }

    // The same string form the intercept hook stores in the tenant column (value.ToString()).
    private static string? TenantOf(IDictionary<string, object?> context, string tenantKey)
        => context.TryGetValue(tenantKey, out var value) ? value?.ToString() : null;

    // The store takes no write from any mutation path (PendingChangeStore.IsWriteProtectedStore),
    // so a rejection or expiry records its own transition, guarded on the row still being pending.
    private async Task TransitionAsync(long id, string state, string? decider, string? reason,
        CancellationToken cancellationToken)
    {
        var store = RequireStore();
        await using var connection = _connections.GetConnection();
        await connection.OpenAsync(cancellationToken);
        var affected = await PendingChangeStore.TransitionFromPendingAsync(
            connection, null, _connections.Dialect, store, id, state, decider, reason, cancellationToken);
        if (affected != 1)
            throw new BifrostExecutionError("The pending change has already been decided.");
    }

    private IDbTable RequireStore() => PendingChangeStore.Find(_model)
        ?? throw new BifrostExecutionError("The pending-changes store is unavailable.");

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
