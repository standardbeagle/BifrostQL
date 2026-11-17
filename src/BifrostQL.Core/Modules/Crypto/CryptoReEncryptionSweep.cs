using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BifrostQL.Core.Crypto;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.Modules.Crypto
{
    /// <summary>Outcome of a re-encryption sweep over a batch of rows.</summary>
    public sealed class CryptoSweepResult
    {
        /// <summary>Rows examined.</summary>
        public int RowsScanned { get; init; }

        /// <summary>Rows that had at least one stale (old-version / legacy) encrypted value and
        /// were routed to an update to re-encrypt under the current DEK version.</summary>
        public int RowsReEncrypted { get; init; }

        /// <summary>The real number of rows the pipeline actually updated (from
        /// <see cref="MutationIntentResult.AffectedRows"/>, NOT the update's return value). Lower
        /// than <see cref="RowsReEncrypted"/> when a tenant/policy scope narrowed a write to zero —
        /// the sweep never bypasses that scope, it fails closed to no write — or when the stored
        /// value changed after the snapshot was read. Such a row is still stale only if the new
        /// write used an old key, so re-running the sweep over a fresh read picks it up.</summary>
        public int RowsAffected { get; init; }
    }

    /// <summary>
    /// Online re-encryption sweep for key rotation. After a key-ref is rotated, values already
    /// stored under an older DEK version stay readable (reads are version-directed) but are not
    /// yet on the current key. This sweep re-encrypts those rows onto the current version.
    ///
    /// <para>The re-encryption WRITE routes exclusively through
    /// <see cref="IMutationIntentExecutor"/> (→ the full <c>TableMutationPipeline</c>), so
    /// <see cref="EncryptOnWriteMutationTransformer"/> performs the encryption under the current
    /// version and the tenant/policy/soft-delete/audit chain applies unconditionally — no direct
    /// SQL, no pipeline bypass. The sweep supplies only the row's positional primary key plus the
    /// decrypted plaintext of each stale encrypted column; it builds NO predicate of its own, so
    /// the pipeline narrows the update from the supplied identity and an out-of-scope row matches
    /// zero rows (the same no-tenant-scope-bypass invariant the Retention purge sweep holds).
    /// Running the sweep with no tenant context on a tenant-filtered table therefore re-encrypts
    /// nothing (fail-closed), never a global cross-tenant rewrite.</para>
    ///
    /// <para>Decryption of the current stored value is version-directed via the same
    /// <see cref="EnvelopeKeyManager"/>: the old DEK version named in the envelope is resolved and
    /// used, never a trial-decrypt. A value already on the current version, or a NULL, is left
    /// untouched, so the sweep is idempotent and safe to re-run over a half-swept table.</para>
    /// </summary>
    public sealed class CryptoReEncryptionSweep
    {
        private readonly IMutationIntentExecutor _writer;
        private readonly EnvelopeKeyManager _keyManager;

        public CryptoReEncryptionSweep(IMutationIntentExecutor writer, EnvelopeKeyManager keyManager)
        {
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            _keyManager = keyManager ?? throw new ArgumentNullException(nameof(keyManager));
        }

        /// <summary>
        /// Re-encrypts every stale encrypted value in <paramref name="rows"/> for
        /// <paramref name="table"/> under the current DEK version. Each row's values are keyed by
        /// GraphQL field name or database column name (case-insensitive) and MUST include the
        /// table's primary-key columns so the update can address the row. Rows whose encrypted
        /// values are all already current (or NULL) are skipped.
        /// </summary>
        public async Task<CryptoSweepResult> ReEncryptRowsAsync(
            IDbTable table,
            IEnumerable<IReadOnlyDictionary<string, object?>> rows,
            IDictionary<string, object?>? userContext = null,
            string? endpoint = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(table);
            ArgumentNullException.ThrowIfNull(rows);

            var encryptedColumns = table.Columns.Where(IsEncrypted).ToList();
            if (encryptedColumns.Count == 0)
                throw new BifrostExecutionError(
                    $"Table '{table.TableSchema}.{table.DbName}' has no encrypted columns to re-encrypt.");

            var keyColumns = table.KeyColumns.ToList();
            if (keyColumns.Count == 0)
                throw new BifrostExecutionError(
                    $"Table '{table.TableSchema}.{table.DbName}' has no primary key; a keyless table cannot be swept.");

            int scanned = 0, reEncrypted = 0, affected = 0;

            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanned++;

                var setData = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                var expectedEnvelopes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var column in encryptedColumns)
                {
                    var raw = ReadColumn(row, column);
                    if (raw is null)
                        continue; // NULL stays NULL — nothing encrypted here.

                    var envelope = raw as string ?? raw.ToString();
                    if (string.IsNullOrEmpty(envelope))
                        continue;

                    var keyRef = column.GetMetadataValue(MetadataKeys.Crypto.KeyRef);
                    if (string.IsNullOrWhiteSpace(keyRef))
                        throw new BifrostExecutionError(
                            $"Encrypted column '{table.TableSchema}.{table.DbName}.{column.ColumnName}' has no key-ref.");

                    var storedVersion = FieldCipher.PeekKeyVersion(envelope); // null == legacy format
                    var currentVersion = await _keyManager.GetCurrentVersionAsync(keyRef, cancellationToken);
                    // Already on the current version ⇒ nothing to do (idempotent / half-swept safe).
                    if (storedVersion == currentVersion)
                        continue;

                    // Version-directed decrypt of the stale value, then hand the PLAINTEXT to the
                    // update so EncryptOnWrite re-encrypts it under the current version.
                    var dek = storedVersion is null
                        ? await _keyManager.GetDataKeyAsync(keyRef, cancellationToken)
                        : await _keyManager.GetDataKeyAsync(keyRef, storedVersion.Value, cancellationToken);
                    var aad = CryptoAad.Build(table.TableSchema, table.DbName, column.ColumnName);
                    setData[column.GraphQlName] = FieldCipher.Decrypt(dek, envelope, aad);
                    expectedEnvelopes[column.ColumnName] = envelope;
                }

                if (setData.Count == 0)
                    continue; // Row is fully current.

                var primaryKey = keyColumns
                    .Select(kc => ReadColumn(row, kc)
                        ?? throw new BifrostExecutionError(
                            $"Row is missing primary-key column '{kc.ColumnName}' required to re-encrypt it."))
                    .ToList();

                // The snapshot's envelopes guard the update (EncryptOnWriteMutationTransformer),
                // so a value written after the snapshot is never overwritten with the old plaintext.
                var rowContext = userContext is null
                    ? new Dictionary<string, object?>()
                    : new Dictionary<string, object?>(userContext);
                rowContext[EncryptOnWriteMutationTransformer.ReEncryptionGuardKey] =
                    new EncryptOnWriteMutationTransformer.ReEncryptionGuard(expectedEnvelopes);

                var result = await _writer.ExecuteAsync(new MutationIntent
                {
                    // Schema-qualified: a bare DbName is ambiguous when another schema holds the same name.
                    Table = $"{table.TableSchema}.{table.DbName}",
                    Action = MutationIntentAction.Update,
                    Data = setData,
                    PrimaryKey = primaryKey,
                    UserContext = rowContext,
                    Endpoint = endpoint,
                }, cancellationToken);

                reEncrypted++;
                // AffectedRows (never Value — that is the PK on a single-key table) is the real
                // count; a scoped-away write reports 0 and is not counted as an effective rewrite.
                affected += result.AffectedRows ?? 0;
            }

            return new CryptoSweepResult
            {
                RowsScanned = scanned,
                RowsReEncrypted = reEncrypted,
                RowsAffected = affected,
            };
        }

        /// <summary>Whether the sweep re-encrypts <paramref name="column"/>; ModelConfigValidator asks the same question.</summary>
        internal static bool IsEncrypted(ColumnDto column)
            => !string.IsNullOrWhiteSpace(column.GetMetadataValue(MetadataKeys.Crypto.Encrypt));

        private static object? ReadColumn(IReadOnlyDictionary<string, object?> row, ColumnDto column)
        {
            if (row.TryGetValue(column.GraphQlName, out var byGraphQl))
                return byGraphQl;
            if (row.TryGetValue(column.ColumnName, out var byDb))
                return byDb;
            return null;
        }
    }
}
