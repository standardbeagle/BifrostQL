using System.Data.Common;
using System.Text.Json;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.Cdc;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Core.Modules.Deferred;

/// <summary>Reviews live deferred changes; rejection is a compensating undo, not maker-checker replay.</summary>
public sealed class DeferredReviewQueue
{
    private readonly IDbModel _model;
    private readonly IDbConnFactory _connections;
    private readonly IMutationIntentExecutor _mutations;
    private readonly PolicyEvaluator _policies;
    private readonly Func<DateTimeOffset> _clock;
    private readonly string? _endpoint;
    private readonly ILogger? _logger;

    public DeferredReviewQueue(IDbModel model, IDbConnFactory connections, IMutationIntentExecutor mutations,
        PolicyEvaluator policies, Func<DateTimeOffset>? clock = null, string? endpoint = null, ILogger? logger = null)
    { _model = model; _connections = connections; _mutations = mutations; _policies = policies; _clock = clock ?? (() => DateTimeOffset.UtcNow); _endpoint = endpoint; _logger = logger; }

    public Task<IReadOnlyList<DeferredReviewItem>> ListAsync(AppIdentity identity, CancellationToken ct = default)
        => ReadVisibleAsync(identity, changeSetId: null, ct);

    public async Task<bool> ApproveAsync(long id, AppIdentity identity, CancellationToken ct = default)
    {
        var item = await LoadAuthorizedAsync(id, identity, ct);
        if (item is null) return false;
        return await new DeferredOutboxReleaseEngine(_model, _connections).ReleaseAsync(id, _clock(), ct) >= 0
            && await StateIsAsync(id, "released", ct);
    }

    public async Task<DeferredUndoResult?> RejectAsync(long id, AppIdentity identity, CancellationToken ct = default)
    {
        if (await LoadAuthorizedAsync(id, identity, ct) is null) return null;
        var mapper = new IdentityContextMapper(TenantFilterTransformer.ResolveTenantContextKey(_model));
        return await new DeferredUndoEngine(_model, _connections, _mutations, _clock, _endpoint)
            .RejectApprovalHoldAsync(id, mapper.ToUserContext(identity), ct);
    }

    // The held, until-approved change sets of the caller's tenant that the caller may read: all of
    // them, or only the one with changeSetId. A tenant-less caller sees the tenant-less change sets
    // (a host without tenants); `tenant = NULL` matched nothing, so such a host could never review.
    private async Task<IReadOnlyList<DeferredReviewItem>> ReadVisibleAsync(
        AppIdentity identity, long? changeSetId, CancellationToken ct)
    {
        var result = new List<DeferredReviewItem>();
        await using var connection = _connections.GetConnection(); await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        var table = RequireChangeSets(); var d = _connections.Dialect;
        var tenantColumn = d.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.Tenant);
        var tenantPredicate = identity.TenantId is null ? $"{tenantColumn} IS NULL" : $"{tenantColumn}=@tenant";
        var idPredicate = changeSetId is null ? "" : $" AND {d.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.Id)}=@id";
        command.CommandText = $"SELECT {d.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.Id)}, {d.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.Requester)}, {tenantColumn}, {d.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.Tables)}, {d.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.CreatedAt)} FROM {d.TableReference(table.TableSchema, table.DbName)} WHERE {d.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.State)}=@held AND {tenantPredicate}{idPredicate}";
        Add(command, "@held", "held");
        if (identity.TenantId is not null) Add(command, "@tenant", identity.TenantId);
        if (changeSetId is not null) Add(command, "@id", changeSetId.Value);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = Convert.ToInt64(reader.GetValue(0));
            try
            {
                var names = JsonSerializer.Deserialize<string[]>(reader.GetString(3)) ?? Array.Empty<string>();
                var tables = names.Select(RequireTable).ToArray();
                if (tables.Length == 0 || tables.Any(t => DeferredConfig.FromTable(t).EventHold != DeferredEventHold.UntilApproved)) continue;
                if (tables.Any(t => !_policies.CanAct(PolicyConfigCollector.FromTable(t), PolicyAction.Read, identity).Allowed)) continue;
                result.Add(new(id, reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), names, DeferredTimestamp.ReadUtc(reader.GetValue(4))));
            }
            catch (Exception ex) when (ex is InvalidOperationException or BifrostExecutionError or JsonException)
            {
                // A change set whose tables are no longer configured, or whose config or policy
                // does not parse, stays hidden (fail closed) — and the operator hears about it.
                _logger?.LogWarning(ex, "Deferred change set {ChangeSetId} is hidden from review: its configuration could not be evaluated.", id);
            }
        }
        return result;
    }

    private async Task<DeferredReviewItem?> LoadAuthorizedAsync(long id, AppIdentity identity, CancellationToken ct)
    {
        var item = (await ReadVisibleAsync(identity, id, ct)).SingleOrDefault();
        if (item is null) return null;
        foreach (var name in item.Tables)
        {
            var table = RequireTable(name);
            var role = table.GetMetadataValue(MetadataKeys.Approval.ApproverRole);
            if (!_policies.CanAct(PolicyConfigCollector.FromTable(table), PolicyAction.Update, identity, role ?? string.Empty).Allowed) return null;
            var self = table.GetMetadataValue(MetadataKeys.Approval.SelfApprove);
            if (MetadataKeys.Approval.FalseValues.Contains(self?.Trim() ?? string.Empty) && string.Equals(item.Requester, identity.Id, StringComparison.Ordinal)) return null;
        }
        return item;
    }

    private async Task<bool> StateIsAsync(long id, string state, CancellationToken ct)
    { await using var c = _connections.GetConnection(); await c.OpenAsync(ct); await using var cmd = c.CreateCommand(); var t = RequireChangeSets(); var d = _connections.Dialect; cmd.CommandText = $"SELECT {d.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.State)} FROM {d.TableReference(t.TableSchema,t.DbName)} WHERE {d.EscapeIdentifier(MetadataKeys.Deferred.ChangeSet.Column.Id)}=@id"; Add(cmd,"@id",id); return string.Equals(Convert.ToString(await cmd.ExecuteScalarAsync(ct)),state,StringComparison.OrdinalIgnoreCase); }
    private IDbTable RequireChangeSets() => RequireTable(MetadataKeys.Deferred.ChangeSet.Table);
    private IDbTable RequireTable(string name) => ModelTableReference.Find(_model, name) ?? throw new BifrostExecutionError("Deferred review configuration is unavailable.");
    private static void Add(DbCommand command,string name,object? value) { var p=command.CreateParameter(); p.ParameterName=name; p.Value=value ?? DBNull.Value; command.Parameters.Add(p); }
}

public sealed record DeferredReviewItem(long ChangeSetId, string? Requester, string? Tenant, IReadOnlyList<string> Tables, DateTimeOffset CreatedAt);
