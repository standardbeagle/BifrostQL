using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using BifrostQL.Core.Crypto;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.Approval;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Core.Modules.Crypto
{
    /// <summary>
    /// Encrypts columns marked with <c>encrypt</c> metadata on INSERT/UPDATE, replacing
    /// the plaintext with an AES-256-GCM ciphertext envelope before any SQL is built, and
    /// populating the deterministic <c>blind-index</c> sibling column for equality search.
    ///
    /// Priority 40 (security band): it runs after tenant/policy pinning and before
    /// soft-delete, so the plaintext is confined to the security band — every downstream
    /// transformer and the SQL layer see only ciphertext. The DEK is resolved per
    /// <c>key-ref</c> from the <see cref="EnvelopeKeyManager"/> in DI; if that manager is
    /// not configured the transformer FAILS CLOSED (aborts the write) rather than persist
    /// plaintext. Plaintext values are never logged.
    /// </summary>
    public sealed class EncryptOnWriteMutationTransformer : IMutationTransformer
    {
        public int Priority => 40;

        public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context)
            => (mutationType == MutationType.Insert || mutationType == MutationType.Update)
               && table.Columns.Any(IsEncrypted);

        public ValueTask<MutationTransformResult> TransformAsync(
            IDbTable table, MutationType mutationType, Dictionary<string, object?> data, MutationTransformContext context)
        {
            // Resolve the key manager lazily from request services so encryption works
            // whether or not the manager was registered at construction time. Its absence
            // for a table that HAS encrypted columns is a fail-closed condition: writing
            // the plaintext would silently defeat the whole feature.
            var keyManager = context.Services?.GetService<EnvelopeKeyManager>();

            var result = new Dictionary<string, object?>(data, StringComparer.OrdinalIgnoreCase);
            var errors = new List<string>();

            // Blind-index columns are derived server-side from their encrypted source;
            // a client-supplied value would desync the search token from the ciphertext
            // (equality search misses the row) or plant a forged token. The schema
            // already omits them from mutation input types; this is the fail-closed
            // backstop for programmatic writers (adapters, intents). An approved replay
            // is exempt: its payload IS the post-transformer data — ciphertext plus the
            // original token — being re-applied verbatim.
            if (!ApprovalInterceptMutationHook.IsApprovedReplay(context.UserContext))
            {
                var blindIndexTargets = table.Columns
                    .Select(c => c.GetMetadataValue(MetadataKeys.Crypto.BlindIndex))
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v!.Trim())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (blindIndexTargets.Count > 0)
                {
                    foreach (var key in data.Keys)
                    {
                        var target = ResolveColumn(table, key);
                        if (target is not null && blindIndexTargets.Contains(target.ColumnName))
                            errors.Add(
                                $"Column '{table.TableSchema}.{table.DbName}.{target.ColumnName}' is a blind-index " +
                                "column derived from its encrypted source column; it cannot be written directly.");
                    }
                }
            }

            foreach (var (key, value) in data)
            {
                var column = ResolveColumn(table, key);
                if (column is null || !IsEncrypted(column))
                    continue;
                if (value is null)
                    continue; // A null is stored as NULL — nothing to encrypt.

                if (ApprovalInterceptMutationHook.IsApprovedReplay(context.UserContext))
                    continue; // Approval payload is post-transformer ciphertext with its original blind index.

                if (keyManager is null)
                {
                    errors.Add(
                        $"Column '{table.TableSchema}.{table.DbName}.{column.ColumnName}' is marked for encryption " +
                        "but no encryption key manager is configured; the write is refused to avoid storing plaintext.");
                    continue;
                }

                var keyRef = column.GetMetadataValue(MetadataKeys.Crypto.KeyRef);
                if (string.IsNullOrWhiteSpace(keyRef))
                {
                    // ModelConfigValidator already rejects this at load; guard anyway.
                    errors.Add(
                        $"Encrypted column '{table.TableSchema}.{table.DbName}.{column.ColumnName}' has no key-ref.");
                    continue;
                }

                // Invariant culture so a decimal/DateTime value serializes to the SAME
                // plaintext (and therefore the SAME deterministic blind-index hash) on
                // every host, regardless of the server/thread culture.
                var plaintext = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                // Encrypt under the key-ref's CURRENT DEK version and stamp that version into
                // the envelope, so the value stays decryptable after the key-ref is rotated.
                var version = keyManager.GetCurrentVersion(keyRef);
                var dek = keyManager.GetDataKey(keyRef, version);
                var aad = CryptoAad.Build(table.TableSchema, table.DbName, column.ColumnName);
                result[key] = FieldCipher.Encrypt(dek, plaintext, aad, version);

                // Populate the blind-index sibling column (if configured) with the keyed
                // deterministic hash of the plaintext, so equality search still works.
                var blindIndexColumn = column.GetMetadataValue(MetadataKeys.Crypto.BlindIndex);
                if (!string.IsNullOrWhiteSpace(blindIndexColumn))
                {
                    // Single-definition derivation shared with the query-time equality
                    // rewrite (BlindIndexComputer.ComputeSearchToken) so write and read
                    // tokens can never drift.
                    result[blindIndexColumn] = BlindIndexComputer.ComputeSearchToken(
                        keyManager, keyRef, table.TableSchema, table.DbName, column.ColumnName, value);
                }
            }

            return ValueTask.FromResult(new MutationTransformResult
            {
                MutationType = mutationType,
                Data = result,
                Errors = errors.ToArray(),
            });
        }

        private static bool IsEncrypted(ColumnDto column)
            => !string.IsNullOrWhiteSpace(column.GetMetadataValue(MetadataKeys.Crypto.Encrypt));

        // Resolves a mutation-data key (GraphQL field name or raw DB column name) to its
        // column, matching how the rest of the pipeline tolerates both name spaces.
        private static ColumnDto? ResolveColumn(IDbTable table, string key)
        {
            if (table.GraphQlLookup.TryGetValue(key, out var byGraphQl))
                return byGraphQl;
            if (table.ColumnLookup.TryGetValue(key, out var byDb))
                return byDb;
            return null;
        }
    }
}
