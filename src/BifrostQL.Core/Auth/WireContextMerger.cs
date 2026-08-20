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
/// the transport, where the model is not yet known.</para>
///
/// <para><b>Identity keys win.</b> A wire entry whose key the identity projection already
/// populated is ignored, so a frontend can only ever ADD non-identity keys.</para>
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
        // silently defaulting would build the owned set around the wrong claim.
        var ownedKeys = new IdentityContextMapper(
            TenantFilterTransformer.ResolveTenantContextKey(model)).OwnedKeyNames;

        foreach (var (key, value) in wireContext)
        {
            if (userContext.ContainsKey(key))
                continue;
            if (ownedKeys.Contains(key) || string.Equals(key, "user", StringComparison.Ordinal))
                continue;
            userContext[key] = value;
        }
    }
}
