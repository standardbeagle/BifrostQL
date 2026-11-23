using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;

namespace BifrostQL.Core.SavedObjects;

/// <summary>
/// Derives the owner partition every <see cref="ISavedObjectStore"/> operation is scoped to.
///
/// <para>The store used to have no owner dimension at all: any accepted caller could list, read,
/// overwrite and delete every other caller's saved objects. On a loopback single-user desktop that
/// was low impact; on any multi-user host it was a straightforward cross-tenant data hole, and
/// "it's usually loopback" is not a reason to leave it.</para>
///
/// <para>The owner is derived ONLY from the caller's projected Bifrost user context — the same
/// <c>IBifrostAuthContextFactory</c> projection every transport gate uses. It is never taken from a
/// request body, header, or path: an owner the client supplies is not isolation, it is a free-form
/// impersonation parameter.</para>
///
/// <para>The token is a hash rather than the plaintext identity, for three reasons: it is a fixed
/// safe alphabet, so it needs no sanitization when it becomes a file path segment (sanitizing a
/// partition key is worse than useless — two distinct owners can sanitize to the SAME segment and
/// silently share a partition); it composes tenant and user unambiguously; and it keeps identifiers
/// out of storage paths and DB rows. The same shape is already used for the Prometheus scrape
/// cache's identity partition.</para>
/// </summary>
public static class SavedObjectOwner
{
    /// <summary>
    /// The single partition shared by callers with no identity at all. Reachable ONLY when a
    /// deployment explicitly clears <c>RequireAuth</c> — the trusted-loopback desktop posture,
    /// where every caller is the same human and a shared partition is the intended semantics.
    /// Four lowercase letters, so it can never collide with the 64-hex token
    /// <see cref="FromUserContext"/> produces for a real identity.
    /// </summary>
    public const string Anonymous = "anon";

    /// <summary>
    /// The owner token for <paramref name="userContext"/>, or <c>null</c> when the projection
    /// carries no stable identity to partition by. Null is a REFUSAL — callers must answer 401,
    /// never fall back to <see cref="Anonymous"/> or to an unscoped store, which would put every
    /// such caller into one shared bucket.
    ///
    /// <para>Partitions by the canonical <c>user_id</c> claim, plus the tenant under
    /// <paramref name="tenantContextKey"/> when the identity carries one, so two tenants whose
    /// providers happen to issue the same subject stay separate. <c>user_id</c> is the fixed key
    /// <c>IdentityContextMapper</c> always writes. The tenant key is the model's
    /// <c>tenant-context-key</c>, resolved by the caller with
    /// <see cref="TenantFilterTransformer.ResolveTenantContextKey"/>: the claim the tenant filter
    /// scopes rows by. Reading the fixed default key instead put two tenants of a deployment that
    /// renamed it (<c>org_id</c>) into one partition whenever their subjects matched. The tenant
    /// is read with <see cref="TenantFilterTransformer.ReadTenantValue(IDictionary{string, object?}, string)"/>,
    /// so a raw one-element claim array is its single value and a multi-valued claim is refused.</para>
    /// </summary>
    public static string? FromUserContext(IDictionary<string, object?>? userContext, string tenantContextKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantContextKey);
        if (userContext is null)
            return null;

        var user = Claim(userContext, MetadataKeys.Auth.DefaultUserIdContextKey);
        if (user is null)
            return null;

        var tenant = TenantFilterTransformer.ReadTenantValue(userContext, tenantContextKey);
        if (string.IsNullOrWhiteSpace(tenant))
            tenant = null;

        // Length-prefixed composition: "u" and "t" values cannot be rearranged into each other,
        // so no two distinct (tenant, user) pairs hash to the same token.
        var composed = $"u{user.Length}:{user}\nt{tenant?.Length ?? -1}:{tenant}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(composed))).ToLowerInvariant();
    }

    /// <summary>
    /// Validates that <paramref name="owner"/> is a token this class produced, and returns it.
    /// Stores call this before using the owner as a storage path segment or key column.
    ///
    /// <para>Deliberately a VALIDATION, not a sanitization. Rewriting an unexpected owner into a
    /// safe one is how two distinct owners end up sharing a partition; refusing it keeps the
    /// partition function injective, which is the whole property isolation rests on.</para>
    /// </summary>
    public static string Require(string owner)
    {
        if (!IsWellFormed(owner))
            throw new ArgumentException(
                "Saved-object owner must be a token produced by SavedObjectOwner " +
                "(the anonymous partition or a 64-character lowercase hex digest).",
                nameof(owner));
        return owner;
    }

    private static bool IsWellFormed(string owner)
    {
        if (string.Equals(owner, Anonymous, StringComparison.Ordinal))
            return true;
        if (owner is not { Length: 64 })
            return false;
        foreach (var ch in owner)
            if (!(ch is >= '0' and <= '9' or >= 'a' and <= 'f'))
                return false;
        return true;
    }

    /// <summary>The claim's value as a non-blank string, or null when absent, null, or blank.</summary>
    private static string? Claim(IDictionary<string, object?> userContext, string key)
    {
        if (!userContext.TryGetValue(key, out var value) || value is null)
            return null;
        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
