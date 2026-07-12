using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using BifrostQL.Core.Crypto;
using BifrostQL.Core.Model;
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

            foreach (var (key, value) in data)
            {
                var column = ResolveColumn(table, key);
                if (column is null || !IsEncrypted(column))
                    continue;
                if (value is null)
                    continue; // A null is stored as NULL — nothing to encrypt.

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
                var dek = keyManager.GetDataKey(keyRef);
                var aad = CryptoAad.Build(table.TableSchema, table.DbName, column.ColumnName);
                result[key] = FieldCipher.Encrypt(dek, plaintext, aad);

                // Populate the blind-index sibling column (if configured) with the keyed
                // deterministic hash of the plaintext, so equality search still works.
                var blindIndexColumn = column.GetMetadataValue(MetadataKeys.Crypto.BlindIndex);
                if (!string.IsNullOrWhiteSpace(blindIndexColumn))
                {
                    var indexKey = keyManager.GetBlindIndexKey(keyRef);
                    result[blindIndexColumn] = BlindIndexComputer.Compute(indexKey, plaintext);
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
