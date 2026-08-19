using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Modules.Approval
{
    /// <summary>
    /// Projects a live request <c>UserContext</c> into the plain, JSON-serializable requester
    /// identity persisted in <c>pending_changes.requester_context</c> and reconstructed by
    /// <see cref="ApprovalDecisionService"/> when an approved change is replayed.
    ///
    /// <para><b>Why a projection and not the context itself.</b> A live user context is not a
    /// value: an authenticated caller's context carries the raw <c>ClaimsPrincipal</c> under
    /// <c>"user"</c> (see <c>BifrostContext</c>) plus other opaque, code-owned entries (the audit
    /// actor override, the replay capability token, a profile). A <c>ClaimsPrincipal</c> is an
    /// object CYCLE — <c>Claim.Subject</c> points back at the <c>ClaimsIdentity</c> holding it —
    /// so serializing the context wholesale throws, which made every AUTHENTICATED caller's
    /// gated write fail while anonymous callers (no principal) succeeded. Persisting the raw
    /// principal would also be wrong even if it serialized: it is a token-derived identity blob,
    /// far more than a replay needs.</para>
    ///
    /// <para><b>What is persisted — exactly what the replay consumes, no more.</b> The replay
    /// re-runs the mutation pipeline under the REQUESTER's scope, and the write-path consumers
    /// of the user context read exactly four things:
    /// the policy subject (<c>user_id</c>, read by <see cref="PolicyIdentity"/>,
    /// <c>PolicyMutationTransformer</c>, <c>SoftDeleteMutationTransformer</c>), the policy roles
    /// (<c>roles</c>), the tenant claim under the model's configured tenant-context key (read by
    /// <c>TenantMutationTransformer</c> and the history/outbox/deferred hooks), and the claim
    /// named by the model's <c>user-audit-key</c> (the requester's audit actor). Everything else
    /// — claim arrays, email and other claim PII, opaque objects — is DROPPED, so the store holds
    /// the minimum identity a replay needs and no token material.</para>
    ///
    /// <para><b>Fail-closed on drop.</b> A key whose value is not a plain scalar/string-sequence
    /// is omitted rather than coerced. A missing tenant claim makes the replay's tenant
    /// transformer refuse the write; a missing subject makes it run as <c>anonymous</c> and be
    /// denied by policy. Omission therefore narrows scope, never widens it.</para>
    /// </summary>
    internal static class RequesterContextProjection
    {
        /// <summary>
        /// Builds the plain requester identity persisted for replay. The result contains only
        /// entries that were present with a plain value, so it never carries a principal.
        /// </summary>
        public static Dictionary<string, object?> Project(IDbModel model, IDictionary<string, object?> userContext)
        {
            if (model is null) throw new ArgumentNullException(nameof(model));
            if (userContext is null) throw new ArgumentNullException(nameof(userContext));

            var projection = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            CopyPlain(userContext, projection, MetadataKeys.Auth.DefaultUserIdContextKey);

            // Normalized to a string array so the stored shape is the one PolicyIdentity and the
            // role gates already accept after a JSON round-trip.
            var roles = PolicyIdentity.ExtractRoles(userContext);
            if (roles.Count > 0)
                projection[MetadataKeys.Auth.DefaultRolesContextKey] = roles.ToArray();

            CopyPlain(userContext, projection, TenantFilterTransformer.ResolveTenantContextKey(model));

            // The requester's audit actor lives under the model-configured claim name, which is
            // frequently the same key as the subject — CopyPlain is idempotent in that case.
            var auditKey = model.GetMetadataValue(MetadataKeys.Audit.UserKey);
            if (!string.IsNullOrWhiteSpace(auditKey))
                CopyPlain(userContext, projection, auditKey);

            return projection;
        }

        private static void CopyPlain(
            IDictionary<string, object?> source, IDictionary<string, object?> target, string? key)
        {
            if (string.IsNullOrWhiteSpace(key) || target.ContainsKey(key))
                return;
            if (!source.TryGetValue(key, out var value))
                return;

            var plain = AsPlainValue(value);
            if (plain is not null)
                target[key] = plain;
        }

        // A value is persisted only when it is a JSON-safe scalar (or a sequence of them).
        // Anything else — a ClaimsPrincipal, an override token, a profile — is dropped rather
        // than coerced through ToString(), so no opaque object can reach the store.
        private static object? AsPlainValue(object? value) => value switch
        {
            null => null,
            string or bool or byte or sbyte or short or ushort or int or uint or long or ulong
                or float or double or decimal or Guid or DateTime or DateTimeOffset => value,
            IEnumerable sequence => sequence.Cast<object?>()
                .Select(AsPlainValue).Where(item => item is not null).ToArray() is { Length: > 0 } items
                    ? items
                    : null,
            _ => null,
        };
    }
}
