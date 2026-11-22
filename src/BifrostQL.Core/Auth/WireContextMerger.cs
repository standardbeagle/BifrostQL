using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;

namespace BifrostQL.Core.Auth;

/// <summary>
/// Merges frontend-parsed WIRE context entries (<see cref="Resolvers.BifrostRequest.WireContext"/>)
/// into a request's user context — the single place that decides which wire entries may reach the
/// transformers.
///
/// <para><b>Identity-owned keys never merge.</b> Roles, tenant, permissions, audit and the raw
/// principal can only ever come from the authenticated identity projection; a wire entry under one
/// of those keys is a spoof attempt whether or not the identity currently carries the key (an
/// omitted tenant slot must not be fillable from the wire). The owned-key set is built
/// MODEL-AWARE: a deployment that configures <c>tenant-context-key</c> (e.g. <c>org_id</c>) gets
/// that key stripped too — the default-key-only strip was bypassable for exactly those
/// deployments, which is why this merge runs in the engine after model resolution rather than at
/// the transport, where the model is not yet known. The same holds for the model's
/// <c>user-audit-key</c>: a wire entry under it would name the actor of the write.</para>
///
/// <para><b>Identity keys win.</b> A wire entry whose key the identity projection already
/// populated is ignored, so a frontend can only ever ADD non-identity keys. Keys compare
/// case-insensitively, as every owned-key check and every downstream context copy does.</para>
/// </summary>
public static class WireContextMerger
{
    public static void Merge(
        IDictionary<string, object?> userContext,
        IDictionary<string, object?>? wireContext,
        IDbModel model)
    {
        if (userContext is null) throw new ArgumentNullException(nameof(userContext));
        if (model is null) throw new ArgumentNullException(nameof(model));
        if (wireContext is not { Count: > 0 })
            return;

        // ResolveTenantContextKey fails fast on a present-but-invalid configured key —
        // silently defaulting would build the owned set around the wrong claim. The model's
        // user-audit-key is owned too: it names the claim every actor column and history row
        // is stamped from, so a wire entry under it would let the client author the write.
        var auditKey = model.GetMetadataValue(MetadataKeys.Audit.UserKey);
        var ownedKeys = new HashSet<string>(new IdentityContextMapper(
            TenantFilterTransformer.ResolveTenantContextKey(model),
            userAuditKey: string.IsNullOrWhiteSpace(auditKey) ? null : auditKey).OwnedKeyNames,
            StringComparer.OrdinalIgnoreCase)
        {
            // A configured tenant-context-key renames what the tenant filter reads, not what the
            // identity mapper writes: tenant_id stays the identity's tenant, read by the audit
            // observer and the policy identity, so it is owned whatever the model configures.
            MetadataKeys.Auth.DefaultTenantContextKey,
        };

        // Keys already present, compared case-insensitively: the audit observer and the workflow
        // trigger/executor rebuild the context with an OrdinalIgnoreCase comparer, so a wire
        // "User" beside the identity's "user" (or two wire keys differing only in case) made that
        // copy throw, and the committed transition went unaudited.
        var presentKeys = new HashSet<string>(userContext.Keys, StringComparer.OrdinalIgnoreCase);

        // Security-resolved context keys: the row-scope placeholders and auto-filter claim
        // keys the model's security filters read at request time. A wire entry for one would
        // let a client whose identity omits the claim supply the scope value itself — the
        // row-scope/auto-filter predicate would run against attacker-chosen input instead of
        // failing closed. These keys are identity, not request.
        var securityKeys = SecurityContextKeyCollector.Collect(model);

        foreach (var (key, value) in wireContext)
        {
            if (presentKeys.Contains(key))
                continue;
            // Engine-internal keys never merge: the workflow-trigger suppression flag and the
            // engine-metrics scrape flag are privileged by the engine's unforgeable markers, and a wire entry under
            // its key is caller-controlled data under an engine-only name.
            if (string.Equals(key, Workflows.WorkflowTriggerHost.SuppressTriggersKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, Observers.EngineMetricsQueryObserver.ScrapeInternalContextKey, StringComparison.OrdinalIgnoreCase))
                continue;
            // The operation-id slot is set only from the Idempotency-Key header by the HTTP
            // middleware; a wire entry would record a write under an id the request never carried.
            if (string.Equals(key, Modules.OperationId.OperationIdConfig.UserContextKey, StringComparison.OrdinalIgnoreCase))
                continue;
            if (ownedKeys.Contains(key) || securityKeys.Contains(key) ||
                string.Equals(key, "user", StringComparison.OrdinalIgnoreCase))
                continue;
            userContext[key] = value;
            presentKeys.Add(key);
        }
    }
}
