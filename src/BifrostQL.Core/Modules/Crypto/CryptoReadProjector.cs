using System;
using System.Collections.Generic;
using System.Linq;
using BifrostQL.Core.Crypto;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Modules.Crypto
{
    /// <summary>
    /// Rewrites encrypted column values on read: decrypts for a caller holding the
    /// column's <c>unmask-role</c> (or the admin role), otherwise returns a masked
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

        private readonly IDbModel _model;
        private readonly EnvelopeKeyManager? _keyManager;
        private readonly HashSet<string> _roles;
        private readonly bool _isAdmin;

        public CryptoReadProjector(IDbModel model, EnvelopeKeyManager? keyManager, IEnumerable<string> callerRoles)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _keyManager = keyManager;
            _roles = new HashSet<string>(callerRoles ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            _isAdmin = _roles.Contains(MetadataKeys.Policy.DefaultAdminRole);
        }

        /// <summary>
        /// Projects <paramref name="raw"/> for the column identified by
        /// <paramref name="fieldName"/> (a GraphQL field name or DB column name) on the
        /// table with DB name <paramref name="tableDbName"/>. Returns <paramref name="raw"/>
        /// unchanged for non-encrypted columns.
        /// </summary>
        public object? Project(string tableDbName, string fieldName, object? raw)
        {
            var column = ResolveColumn(tableDbName, fieldName);
            if (column is null)
                return raw;

            var algorithm = column.GetMetadataValue(MetadataKeys.Crypto.Encrypt);
            if (string.IsNullOrWhiteSpace(algorithm))
                return raw; // Not an encrypted column.

            if (raw is null)
                return null;

            var table = _model.GetTableFromDbName(tableDbName);
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
                var dek = _keyManager.GetDataKey(keyRef);
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

        private ColumnDto? ResolveColumn(string tableDbName, string fieldName)
        {
            IDbTable table;
            try { table = _model.GetTableFromDbName(tableDbName); }
            catch { return null; }
            if (table is null)
                return null;
            if (table.GraphQlLookup.TryGetValue(fieldName, out var byGraphQl))
                return byGraphQl;
            if (table.ColumnLookup.TryGetValue(fieldName, out var byDb))
                return byDb;
            return null;
        }
    }
}
