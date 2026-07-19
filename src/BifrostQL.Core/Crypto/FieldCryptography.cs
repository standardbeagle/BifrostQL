using System;
using System.Security.Cryptography;
using System.Text;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Crypto
{
    /// <summary>
    /// A parsed <c>key-ref</c> metadata value in <c>provider:id</c> form (e.g.
    /// <c>kms:pii</c>, <c>config:pii</c>). The provider selects where the root key
    /// lives; the id names one data-encryption key within it.
    /// </summary>
    public readonly record struct KeyRef(string Provider, string Id)
    {
        /// <summary>The canonical <c>provider:id</c> string.</summary>
        public override string ToString() => $"{Provider}:{Id}";

        /// <summary>
        /// Parses and validates a <c>key-ref</c>. The provider must be a recognized
        /// one (<see cref="MetadataKeys.Crypto.KeyRefProviders"/>) and the id must be
        /// non-empty. Returns false (not throw) on a malformed value so callers can
        /// surface a descriptive validation error.
        /// </summary>
        public static bool TryParse(string? raw, out KeyRef keyRef)
        {
            keyRef = default;
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            var idx = raw.IndexOf(':');
            if (idx <= 0 || idx == raw.Length - 1)
                return false;

            var provider = raw[..idx].Trim();
            var id = raw[(idx + 1)..].Trim();
            if (provider.Length == 0 || id.Length == 0)
                return false;
            if (!MetadataKeys.Crypto.KeyRefProviders.Contains(provider))
                return false;

            keyRef = new KeyRef(provider, id);
            return true;
        }
    }

    /// <summary>
    /// Builds the Additional Authenticated Data (AAD) that binds a ciphertext to the
    /// column it belongs to. AES-GCM authenticates but does not encrypt the AAD, so
    /// decryption fails if a ciphertext is copy-pasted to a different column or table —
    /// closing the "relocate an admin's encrypted SSN into another column" attack.
    ///
    /// The binding is column-scoped (schema + table + column), NOT per-row: a row's
    /// primary key is not available at encrypt time for a database-generated key
    /// (encryption runs before the INSERT that mints the id), so binding to it would
    /// make write and read asymmetric. Per-row binding is a documented future
    /// enhancement requiring a post-insert re-encrypt or an AAD-kind envelope flag.
    /// Column-scoping is length-prefixed so component boundaries are unambiguous even
    /// when a name contains the delimiter.
    /// </summary>
    public static class CryptoAad
    {
        /// <summary>AAD = length-prefixed UTF-8 of (schema, table, column).</summary>
        public static byte[] Build(string schema, string table, string column)
        {
            using var ms = new System.IO.MemoryStream();
            Span<byte> len = stackalloc byte[4];
            foreach (var part in new[] { schema, table, column })
            {
                var bytes = Encoding.UTF8.GetBytes(part ?? string.Empty);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(len, bytes.Length);
                ms.Write(len);
                ms.Write(bytes);
            }
            return ms.ToArray();
        }
    }

    /// <summary>
    /// AES-256-GCM authenticated encryption of a single field value with a caller
    /// -supplied 32-byte data-encryption key (DEK) and cell-binding AAD. Produces a
    /// self-describing base64 envelope whose leading byte is the ENVELOPE FORMAT
    /// version (not the key version):
    /// <list type="bullet">
    /// <item><c>[1][nonce:12][tag:16][ciphertext:n]</c> — legacy format, no embedded
    /// key version. Written before key rotation existed; the DEK it was encrypted under
    /// is the key-ref's original (version 1 / unversioned) DEK.</item>
    /// <item><c>[2][keyVersion:4 LE][nonce:12][tag:16][ciphertext:n]</c> — versioned
    /// format. The 4-byte little-endian key version travels WITH the ciphertext so a
    /// value written under an old DEK still decrypts after the key-ref is rotated: the
    /// reader resolves the exact DEK version named here (version-directed), never trial
    /// -decrypting against a set of candidate keys.</item>
    /// </list>
    /// A fresh random nonce per call makes the ciphertext non-deterministic (equal
    /// plaintexts encrypt differently) — equality search must go through a blind
    /// index (<see cref="BlindIndexComputer"/>), never the ciphertext.
    /// </summary>
    public static class FieldCipher
    {
        /// <summary>Legacy envelope format: no embedded key version (pre-rotation writes).</summary>
        public const byte FormatLegacy = 1;

        /// <summary>Versioned envelope format: a 4-byte key version follows the format byte.</summary>
        public const byte FormatVersioned = 2;

        /// <summary>Retained for source compatibility; equals <see cref="FormatLegacy"/>.</summary>
        public const byte Version = FormatLegacy;

        public const int KeySize = 32;   // AES-256
        public const int NonceSize = 12; // GCM standard nonce
        public const int TagSize = 16;   // GCM standard tag
        public const int KeyVersionSize = 4; // little-endian int32 key version

        /// <summary>
        /// Encrypts <paramref name="plaintext"/> under a specific <paramref name="keyVersion"/>,
        /// returning the base64 VERSIONED envelope (<see cref="FormatVersioned"/>). The key
        /// version is embedded so the value stays decryptable after the key-ref rotates.
        /// </summary>
        public static string Encrypt(ReadOnlySpan<byte> dek, string plaintext, ReadOnlySpan<byte> aad, int keyVersion)
        {
            RequireKey(dek);
            ArgumentNullException.ThrowIfNull(plaintext);
            if (keyVersion < 1)
                throw new ArgumentOutOfRangeException(nameof(keyVersion), keyVersion, "Key version must be >= 1.");

            var plainBytes = Encoding.UTF8.GetBytes(plaintext);
            var nonce = new byte[NonceSize];
            RandomNumberGenerator.Fill(nonce);
            var cipherBytes = new byte[plainBytes.Length];
            var tag = new byte[TagSize];

            using var aes = new AesGcm(dek, TagSize);
            aes.Encrypt(nonce, plainBytes, cipherBytes, tag, aad);

            var envelope = new byte[1 + KeyVersionSize + NonceSize + TagSize + cipherBytes.Length];
            envelope[0] = FormatVersioned;
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(envelope.AsSpan(1, KeyVersionSize), keyVersion);
            nonce.CopyTo(envelope, 1 + KeyVersionSize);
            tag.CopyTo(envelope, 1 + KeyVersionSize + NonceSize);
            cipherBytes.CopyTo(envelope, 1 + KeyVersionSize + NonceSize + TagSize);
            return Convert.ToBase64String(envelope);
        }

        /// <summary>
        /// Encrypts <paramref name="plaintext"/> into a LEGACY envelope (<see cref="FormatLegacy"/>,
        /// no embedded key version). Retained so existing callers/tests round-trip and so legacy
        /// stored ciphertext can be reproduced; the production write path uses the versioned
        /// overload. The DEK supplied is the key-ref's original (version 1) DEK.
        /// </summary>
        public static string Encrypt(ReadOnlySpan<byte> dek, string plaintext, ReadOnlySpan<byte> aad)
        {
            RequireKey(dek);
            ArgumentNullException.ThrowIfNull(plaintext);

            var plainBytes = Encoding.UTF8.GetBytes(plaintext);
            var nonce = new byte[NonceSize];
            RandomNumberGenerator.Fill(nonce);
            var cipherBytes = new byte[plainBytes.Length];
            var tag = new byte[TagSize];

            using var aes = new AesGcm(dek, TagSize);
            aes.Encrypt(nonce, plainBytes, cipherBytes, tag, aad);

            var envelope = new byte[1 + NonceSize + TagSize + cipherBytes.Length];
            envelope[0] = FormatLegacy;
            nonce.CopyTo(envelope, 1);
            tag.CopyTo(envelope, 1 + NonceSize);
            cipherBytes.CopyTo(envelope, 1 + NonceSize + TagSize);
            return Convert.ToBase64String(envelope);
        }

        /// <summary>
        /// Returns the embedded key version of a versioned envelope, or <c>null</c> for a legacy
        /// envelope (which has none). The read path calls this to resolve the EXACT DEK version to
        /// decrypt with BEFORE calling <see cref="Decrypt"/> — decryption is version-directed, never
        /// a trial-decrypt across candidate keys. Throws on a malformed/short/unknown-format envelope.
        /// </summary>
        public static int? PeekKeyVersion(string envelopeBase64)
        {
            var envelope = DecodeEnvelope(envelopeBase64);
            return envelope[0] switch
            {
                FormatLegacy => null,
                FormatVersioned => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(envelope.AsSpan(1, KeyVersionSize)),
                _ => throw new CryptographicException($"Unsupported ciphertext envelope format {envelope[0]}."),
            };
        }

        /// <summary>
        /// Decrypts a base64 envelope (either format). Throws <see cref="CryptographicException"/>
        /// when the DEK is wrong, the AAD does not match the cell, or the ciphertext/tag was
        /// tampered with — GCM authentication is the integrity guarantee. The caller is responsible
        /// for supplying the DEK matching the envelope's key version (see <see cref="PeekKeyVersion"/>);
        /// this method performs a single directed decrypt, never a trial across keys.
        /// </summary>
        public static string Decrypt(ReadOnlySpan<byte> dek, string envelopeBase64, ReadOnlySpan<byte> aad)
        {
            RequireKey(dek);
            var envelope = DecodeEnvelope(envelopeBase64);

            int headerLen = envelope[0] switch
            {
                FormatLegacy => 1,
                FormatVersioned => 1 + KeyVersionSize,
                _ => throw new CryptographicException($"Unsupported ciphertext envelope format {envelope[0]}."),
            };
            if (envelope.Length < headerLen + NonceSize + TagSize)
                throw new CryptographicException("Ciphertext envelope is too short to be valid.");

            var nonce = envelope.AsSpan(headerLen, NonceSize);
            var tag = envelope.AsSpan(headerLen + NonceSize, TagSize);
            var cipher = envelope.AsSpan(headerLen + NonceSize + TagSize);
            var plain = new byte[cipher.Length];

            using var aes = new AesGcm(dek, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain, aad); // throws on auth failure
            return Encoding.UTF8.GetString(plain);
        }

        private static byte[] DecodeEnvelope(string envelopeBase64)
        {
            ArgumentException.ThrowIfNullOrEmpty(envelopeBase64);
            byte[] envelope;
            try { envelope = Convert.FromBase64String(envelopeBase64); }
            catch (FormatException ex) { throw new CryptographicException("Ciphertext envelope is not valid base64.", ex); }
            if (envelope.Length < 1)
                throw new CryptographicException("Ciphertext envelope is empty.");
            return envelope;
        }

        private static void RequireKey(ReadOnlySpan<byte> dek)
        {
            if (dek.Length != KeySize)
                throw new ArgumentException($"A data-encryption key must be {KeySize} bytes (AES-256).", nameof(dek));
        }
    }

    /// <summary>
    /// Deterministic keyed hash of a plaintext value for equality search on an
    /// encrypted column (a "blind index"). HMAC-SHA-256 with a per-column index key
    /// derived from the DEK — deterministic so the same value always hashes the same,
    /// keyed so an attacker without the index key cannot precompute a rainbow table.
    /// The hash is stored in the sibling <c>blind-index</c> column and matched on
    /// equality; it is one-way (the plaintext is never recoverable from it).
    /// </summary>
    public static class BlindIndexComputer
    {
        /// <summary>
        /// Computes the lowercase-hex HMAC-SHA-256 of <paramref name="value"/> under
        /// <paramref name="indexKey"/>. The caller is responsible for normalizing
        /// <paramref name="value"/> (e.g. case/whitespace) identically on write and on
        /// query, or equal-but-differently-formatted values will not match.
        /// </summary>
        public static string Compute(ReadOnlySpan<byte> indexKey, string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            Span<byte> hash = stackalloc byte[32];
            HMACSHA256.HashData(indexKey, Encoding.UTF8.GetBytes(value), hash);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
