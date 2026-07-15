using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using BifrostQL.Core.Model;
using BifrostQL.Core.Utils;

namespace BifrostQL.Core.Modules.Cdc
{
    /// <summary>
    /// Parsed model-level CDC subscription that scopes outbound delivery BEFORE a
    /// drained outbox row reaches any <see cref="IEventSink"/>. Built from the three
    /// <c>subscription-*</c> model keys (<see cref="MetadataKeys.Cdc.SubscriptionTables"/>
    /// / <see cref="MetadataKeys.Cdc.SubscriptionTenant"/> /
    /// <see cref="MetadataKeys.Cdc.SubscriptionRedact"/>).
    ///
    /// <para><b>Backward-compat (load-bearing).</b> A model that declares NONE of the
    /// three keys yields <see cref="Unrestricted"/> (<see cref="Active"/> = false):
    /// <see cref="Delivers"/> returns true for every row and <see cref="Redact"/> is a
    /// no-op, preserving the pre-subscription deliver-all behaviour so a non-subscribing
    /// host is entirely unaffected.</para>
    ///
    /// <para><b>Fail-closed when active.</b> Once any key is set, delivery is gated: the
    /// row's aggregate MUST be on the allow-list (an empty allow-list delivers NOTHING —
    /// never everything), and if a tenant is bound the row's tenant must ordinally equal
    /// it (a null/unknown tenant is never delivered to a bound subscription).</para>
    /// </summary>
    public sealed class CdcSubscription
    {
        /// <summary>The deliver-all sentinel for a host that declares no subscription.</summary>
        public static readonly CdcSubscription Unrestricted = new(
            active: false,
            allowedAggregates: new HashSet<string>(),
            tenant: null,
            redactColumns: new HashSet<string>());

        // Normalized (NormalizeName) qualified source-table names on the allow-list.
        private readonly IReadOnlySet<string> _allowedAggregates;
        // Trimmed bound tenant id compared ordinally against the outbox row's tenant; null = unbound.
        private readonly string? _tenant;
        // Normalized (NormalizeName) column names stripped from every payload.
        private readonly IReadOnlySet<string> _redactColumns;

        private CdcSubscription(
            bool active,
            IReadOnlySet<string> allowedAggregates,
            string? tenant,
            IReadOnlySet<string> redactColumns)
        {
            Active = active;
            _allowedAggregates = allowedAggregates;
            _tenant = tenant;
            _redactColumns = redactColumns;
        }

        /// <summary>Whether any <c>subscription-*</c> key is declared (delivery is scoped).</summary>
        public bool Active { get; }

        /// <summary>
        /// Whether an outbox row for <paramref name="aggregate"/> with tenant
        /// <paramref name="rowTenant"/> is delivered. <see cref="Unrestricted"/> delivers
        /// everything; an active subscription delivers only allow-listed aggregates and,
        /// when tenant-bound, only rows whose tenant ordinally matches (never a
        /// null/blank/mismatched tenant).
        /// </summary>
        public bool Delivers(string aggregate, string? rowTenant)
        {
            if (!Active)
                return true;

            // Empty allow-list ⇒ nothing matches ⇒ fail-closed (deliver nothing).
            if (!_allowedAggregates.Contains(StringNormalizer.NormalizeName(aggregate)))
                return false;

            if (_tenant is not null)
            {
                // A null/blank-tenant row is NEVER delivered to a tenant-bound subscription.
                if (string.IsNullOrWhiteSpace(rowTenant))
                    return false;
                if (!string.Equals(rowTenant, _tenant, StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Strips every property named in the redaction list from <paramref name="payload"/>,
        /// EXCEPT any name that is also a primary-key column (<paramref name="keyColumns"/>) —
        /// removing a key column would corrupt the CloudEvents subject and consumer identity.
        /// A no-op when <see cref="Active"/> is false or the redaction list is empty. Mutates
        /// and returns the same object.
        /// </summary>
        public JsonObject Redact(JsonObject payload, IEnumerable<string> keyColumns)
        {
            if (payload is null)
                throw new ArgumentNullException(nameof(payload));

            if (!Active || _redactColumns.Count == 0)
                return payload;

            var protectedKeys = new HashSet<string>(
                (keyColumns ?? Enumerable.Empty<string>()).Select(StringNormalizer.NormalizeName));

            // Snapshot the names first: a JsonObject cannot be mutated while enumerated.
            var toRemove = payload
                .Select(p => p.Key)
                .Where(name =>
                {
                    var normalized = StringNormalizer.NormalizeName(name);
                    return _redactColumns.Contains(normalized) && !protectedKeys.Contains(normalized);
                })
                .ToList();

            foreach (var name in toRemove)
                payload.Remove(name);

            return payload;
        }

        /// <summary>
        /// Parses the model-level subscription config. Returns <see cref="Unrestricted"/>
        /// when no <c>subscription-*</c> key is declared. Throws
        /// <see cref="InvalidOperationException"/> on a present-but-empty list key (e.g.
        /// <c>","</c>) — mirroring <see cref="CdcEventConfig"/>'s fail-fast guard — so a
        /// malformed value fails at model load rather than silently scoping to nothing.
        /// </summary>
        public static CdcSubscription FromModel(IDbModel model)
        {
            if (model is null)
                throw new ArgumentNullException(nameof(model));

            var tablesRaw = model.GetMetadataValue(MetadataKeys.Cdc.SubscriptionTables);
            var tenantRaw = model.GetMetadataValue(MetadataKeys.Cdc.SubscriptionTenant);
            var redactRaw = model.GetMetadataValue(MetadataKeys.Cdc.SubscriptionRedact);

            var active = !string.IsNullOrWhiteSpace(tablesRaw)
                      || !string.IsNullOrWhiteSpace(tenantRaw)
                      || !string.IsNullOrWhiteSpace(redactRaw);
            if (!active)
                return Unrestricted;

            var allowed = ParseList(tablesRaw, MetadataKeys.Cdc.SubscriptionTables)
                .Select(StringNormalizer.NormalizeName)
                .ToHashSet();
            var redact = ParseList(redactRaw, MetadataKeys.Cdc.SubscriptionRedact)
                .Select(StringNormalizer.NormalizeName)
                .ToHashSet();
            var tenant = string.IsNullOrWhiteSpace(tenantRaw) ? null : tenantRaw.Trim();

            return new CdcSubscription(active: true, allowed, tenant, redact);
        }

        // Splits a comma list. A whitespace-only/absent value reads as an empty list (a
        // tenant-only subscription, say, has no allow-list and so delivers nothing). A
        // NON-blank value that trims to zero entries (e.g. "," or ", ,") is malformed and
        // fails fast, mirroring CdcEventConfig's "present but empty" guard.
        private static IReadOnlyList<string> ParseList(string? raw, string key)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return Array.Empty<string>();

            var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
                throw new InvalidOperationException(
                    $"'{key}' is set but names no entries. Provide a comma-separated list of names, " +
                    "or remove the key.");
            return parts;
        }
    }
}
