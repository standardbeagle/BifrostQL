using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BifrostQL.Core.Crypto;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Modules.Crypto
{
    /// <summary>
    /// Rewrites encrypted column values on read: decrypts for a caller holding the
    /// column's <c>unmask-role</c> (or the model's <c>:root { policy-admin-role }</c>), otherwise returns a masked
    /// value per the column's <c>mask</c> mode. The raw ciphertext is NEVER returned
    /// to the client — if decryption is impossible (no key manager, wrong/absent key,
    /// tampered value) the projector falls back to a redaction, so a misconfiguration
    /// hides the value rather than leaking ciphertext.
    ///
    /// Non-encrypted columns pass through untouched. Masking of <c>last4</c>/<c>email</c>
    /// decrypts server-side to compute the masked form; only the masked value leaves the
    /// process. Built per query from the caller's roles, so two callers of the same row
    /// see different projections.
    /// </summary>
    public sealed class CryptoReadProjector
    {
        private const string Redacted = "••••••";

        private readonly EnvelopeKeyManager? _keyManager;
        private readonly HashSet<string> _roles;
        private readonly bool _isAdmin;

        /// <summary>
        /// Builds a projector for a caller's roles after loading every DEK version the model's
        /// encrypted columns can name, so <see cref="Project"/> decrypts per row without I/O.
        /// </summary>
        public static async ValueTask<CryptoReadProjector> CreateAsync(IDbModel model, EnvelopeKeyManager? keyManager,
            IEnumerable<string> callerRoles, CancellationToken cancellationToken = default)
        {
            await WarmAsync(model, keyManager, cancellationToken);
            return new CryptoReadProjector(model, keyManager, callerRoles, unmaskAll: false);
        }

        /// <summary>
        /// A projector that decrypts every column for the engine's own reads (the change
        /// diff), whose rows never leave the process. Not reachable from a caller's roles.
        /// </summary>
        internal static async ValueTask<CryptoReadProjector> CreateUnmaskedAsync(IDbModel model,
            EnvelopeKeyManager? keyManager, CancellationToken cancellationToken = default)
        {
            await WarmAsync(model, keyManager, cancellationToken);
            return new CryptoReadProjector(model, keyManager, Array.Empty<string>(), unmaskAll: true);
        }

        private static async ValueTask WarmAsync(IDbModel model, EnvelopeKeyManager? keyManager,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(model);
            if (keyManager is null)
                return;
            var keyRefs = model.Tables
                .SelectMany(table => table.Columns)
                .Where(column => !string.IsNullOrWhiteSpace(column.GetMetadataValue(MetadataKeys.Crypto.Encrypt)))
                .Select(column => column.GetMetadataValue(MetadataKeys.Crypto.KeyRef))
                .Where(keyRef => !string.IsNullOrWhiteSpace(keyRef))
                .Select(keyRef => keyRef!)
                .ToList();
            if (keyRefs.Count > 0)
                await keyManager.WarmAsync(keyRefs, cancellationToken);
        }

        private CryptoReadProjector(IDbModel model, EnvelopeKeyManager? keyManager, IEnumerable<string> callerRoles, bool unmaskAll)
        {
            _keyManager = keyManager;
            _roles = new HashSet<string>(callerRoles ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            _isAdmin = unmaskAll || Auth.PolicyEvaluator.AdminRoleOf(model) is { } adminRole && _roles.Contains(adminRole);
        }

        /// <summary>
        /// Projects <paramref name="raw"/> for the column identified by
        /// <paramref name="fieldName"/> (a GraphQL field name or DB column name) on
        /// <paramref name="table"/>. Returns <paramref name="raw"/> unchanged for
        /// non-encrypted columns. The caller passes the resolved table, never a bare name:
        /// one name can exist in two schemas, and a lookup miss here would skip decryption
        /// and masking and return the stored value as-is.
        /// </summary>
        public object? Project(IDbTable table, string fieldName, object? raw)
        {
            var column = ResolveColumn(table, fieldName);
            if (column is null)
                return raw;

            var algorithm = column.GetMetadataValue(MetadataKeys.Crypto.Encrypt);
            if (string.IsNullOrWhiteSpace(algorithm))
                return raw; // Not an encrypted column.

            if (raw is null)
                return null;

            var keyRef = column.GetMetadataValue(MetadataKeys.Crypto.KeyRef);
            var canUnmask = _isAdmin || HoldsUnmaskRole(column);

            if (canUnmask)
                return TryDecrypt(table, column, keyRef, raw.ToString()) ?? Redacted;

            // Masked callers: redact needs no plaintext; last4/email need the plaintext
            // to derive the masked form, so decrypt server-side and mask the result.
            var maskMode = column.GetMetadataValue(MetadataKeys.Crypto.Mask) ?? MetadataKeys.Crypto.MaskRedact;
            if (string.Equals(maskMode, MetadataKeys.Crypto.MaskRedact, StringComparison.OrdinalIgnoreCase))
                return Redacted;

            var plaintext = TryDecrypt(table, column, keyRef, raw.ToString());
            return plaintext is null ? Redacted : Mask(maskMode, plaintext);
        }

        private bool HoldsUnmaskRole(ColumnDto column)
        {
            var unmaskRole = column.GetMetadataValue(MetadataKeys.Crypto.UnmaskRole);
            // No unmask-role configured ⇒ only admin sees plaintext (fail-closed).
            return !string.IsNullOrWhiteSpace(unmaskRole) && _roles.Contains(unmaskRole);
        }

        private string? TryDecrypt(IDbTable table, ColumnDto column, string? keyRef, string? envelope)
        {
            if (_keyManager is null || string.IsNullOrWhiteSpace(keyRef) || string.IsNullOrEmpty(envelope))
                return null;
            try
            {
                // Version-directed: resolve the EXACT DEK version stamped in the envelope so a
                // value written under an old key still decrypts after rotation. A legacy
                // (unversioned-format) envelope has no version and uses the original DEK. We
                // never trial-decrypt across candidate keys. The DEKs were loaded when the
                // projector was created; a version minted after that (a rotation elsewhere
                // mid-request) is not in the cache and redacts.
                var version = FieldCipher.PeekKeyVersion(envelope);
                if (!_keyManager.TryGetCachedDataKey(keyRef, version, out var dek))
                    return null;
                var aad = CryptoAad.Build(table.TableSchema, table.DbName, column.ColumnName);
                return FieldCipher.Decrypt(dek, envelope, aad);
            }
            catch (Exception)
            {
                // Never surface ciphertext or crypto errors as data. A decrypt failure
                // (wrong key, tampered value, legacy plaintext) redacts.
                return null;
            }
        }

        private static string Mask(string maskMode, string plaintext)
        {
            if (string.Equals(maskMode, MetadataKeys.Crypto.MaskLast4, StringComparison.OrdinalIgnoreCase))
            {
                // Only reveal the last 4 when at least one character stays hidden.
                // A value of 4 chars or fewer would otherwise be shown in full — a leak
                // of the whole plaintext (e.g. a 4-digit PIN) — so redact it entirely.
                return plaintext.Length <= 4 ? Redacted : "••••" + plaintext[^4..];
            }

            if (string.Equals(maskMode, MetadataKeys.Crypto.MaskEmail, StringComparison.OrdinalIgnoreCase))
            {
                var at = plaintext.IndexOf('@');
                if (at <= 0)
                    return Redacted; // Not an email shape — redact rather than reveal.
                var firstChar = plaintext[0];
                return $"{firstChar}••••{plaintext[at..]}";
            }

            return Redacted;
        }

        private static ColumnDto? ResolveColumn(IDbTable table, string fieldName)
        {
            if (table.GraphQlLookup.TryGetValue(fieldName, out var byGraphQl))
                return byGraphQl;
            if (table.ColumnLookup.TryGetValue(fieldName, out var byDb))
                return byDb;
            return null;
        }
    }
}
