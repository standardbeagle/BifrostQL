using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace BifrostQL.Core.Crypto
{
    /// <summary>
    /// Supplies the 32-byte root (master) key that wraps every data-encryption key
    /// (DEK). The root key never encrypts field data directly — it only wraps DEKs —
    /// so rotating a compromised root key re-wraps DEKs without re-encrypting data.
    /// Implementations back onto config/env (<see cref="ConfigRootKeyProvider"/>) or a
    /// KMS/HSM (a later provider); the manager never sees where the key came from.
    /// </summary>
    public interface IRootKeyProvider
    {
        /// <summary>Returns the 32-byte root key. Must be exactly 32 bytes (AES-256).</summary>
        byte[] GetRootKey();
    }

    /// <summary>
    /// Root key held in memory, sourced from configuration/environment (e.g. a base64
    /// secret injected at deploy time). The bytes are copied defensively so a caller
    /// cannot mutate the provider's key.
    /// </summary>
    public sealed class ConfigRootKeyProvider : IRootKeyProvider
    {
        private readonly byte[] _key;

        public ConfigRootKeyProvider(byte[] key)
        {
            ArgumentNullException.ThrowIfNull(key);
            if (key.Length != FieldCipher.KeySize)
                throw new ArgumentException($"The root key must be {FieldCipher.KeySize} bytes (AES-256).", nameof(key));
            _key = (byte[])key.Clone();
        }

        /// <summary>Builds a provider from a base64-encoded 32-byte key.</summary>
        public static ConfigRootKeyProvider FromBase64(string base64)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(base64);
            byte[] key;
            try { key = Convert.FromBase64String(base64); }
            catch (FormatException ex) { throw new ArgumentException("The root key is not valid base64.", nameof(base64), ex); }
            return new ConfigRootKeyProvider(key);
        }

        public byte[] GetRootKey() => (byte[])_key.Clone();
    }

    /// <summary>
    /// Persists wrapped (root-key-encrypted) DEKs keyed by their <c>key-ref</c>. Only
    /// ever holds ciphertext — the plaintext DEK exists solely in memory inside
    /// <see cref="EnvelopeKeyManager"/>. A production store backs onto a table or the
    /// KMS; the in-memory implementation is for tests and single-process defaults.
    /// </summary>
    public interface IDataEncryptionKeyStore
    {
        /// <summary>Returns the wrapped DEK for <paramref name="keyRef"/>, or null if none exists yet.</summary>
        byte[]? Load(string keyRef);

        /// <summary>Stores the wrapped DEK for <paramref name="keyRef"/>, overwriting any prior value.</summary>
        void Store(string keyRef, byte[] wrappedDek);
    }

    /// <summary>Process-memory wrapped-DEK store. Thread-safe. Not durable across restarts.</summary>
    public sealed class InMemoryDataEncryptionKeyStore : IDataEncryptionKeyStore
    {
        private readonly ConcurrentDictionary<string, byte[]> _wrapped = new(StringComparer.Ordinal);

        public byte[]? Load(string keyRef)
            => _wrapped.TryGetValue(keyRef, out var v) ? (byte[])v.Clone() : null;

        public void Store(string keyRef, byte[] wrappedDek)
            => _wrapped[keyRef] = (byte[])wrappedDek.Clone();
    }

    /// <summary>
    /// Envelope key manager: resolves a <c>key-ref</c> to its plaintext DEK by
    /// unwrapping the stored wrapped-DEK with the root key, generating and wrapping a
    /// fresh random DEK on first use. Wrapping is AES-256-GCM with the key-ref bound as
    /// AAD, so a wrapped DEK cannot be relocated to another key-ref. Unwrapped DEKs are
    /// cached in memory (standard envelope-encryption practice) so hot columns do not
    /// re-unwrap per row.
    /// </summary>
    public sealed class EnvelopeKeyManager
    {
        private readonly IRootKeyProvider _rootKeys;
        private readonly IDataEncryptionKeyStore _store;
        private readonly ConcurrentDictionary<string, byte[]> _dekCache = new(StringComparer.Ordinal);
        private readonly object _generateLock = new();

        public EnvelopeKeyManager(IRootKeyProvider rootKeys, IDataEncryptionKeyStore store)
        {
            _rootKeys = rootKeys ?? throw new ArgumentNullException(nameof(rootKeys));
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>
        /// Returns the 32-byte plaintext DEK for <paramref name="keyRef"/>, unwrapping
        /// (or generating + wrapping on first use) as needed. The returned array is a
        /// copy the caller may use freely.
        /// </summary>
        public byte[] GetDataKey(string keyRef)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(keyRef);

            if (_dekCache.TryGetValue(keyRef, out var cached))
                return (byte[])cached.Clone();

            // Serialize the load-or-generate so two concurrent first-uses of the same
            // key-ref cannot each generate (and store) a different DEK — that would
            // orphan data encrypted under the loser's key.
            lock (_generateLock)
            {
                if (_dekCache.TryGetValue(keyRef, out cached))
                    return (byte[])cached.Clone();

                var rootKey = _rootKeys.GetRootKey();
                var wrapped = _store.Load(keyRef);
                byte[] dek;
                if (wrapped is null)
                {
                    dek = RandomNumberGenerator.GetBytes(FieldCipher.KeySize);
                    _store.Store(keyRef, Wrap(rootKey, dek, keyRef));
                }
                else
                {
                    dek = Unwrap(rootKey, wrapped, keyRef);
                }

                _dekCache[keyRef] = dek;
                return (byte[])dek.Clone();
            }
        }

        /// <summary>
        /// Derives the per-column blind-index key from the DEK for <paramref name="keyRef"/>
        /// via HKDF-SHA-256, so the deterministic index key is distinct from the DEK that
        /// encrypts the data (compromising one does not reveal the other).
        /// </summary>
        public byte[] GetBlindIndexKey(string keyRef)
        {
            var dek = GetDataKey(keyRef);
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, dek, outputLength: 32, info: BlindIndexInfo);
        }

        private static readonly byte[] BlindIndexInfo = Encoding.UTF8.GetBytes("bifrost-blind-index-v1");

        // Wrap/unwrap use AES-256-GCM with the key-ref bound as AAD. Layout mirrors the
        // field envelope minus the version byte: [nonce:12][tag:16][ciphertext:32].
        private static byte[] Wrap(byte[] rootKey, byte[] dek, string keyRef)
        {
            var aad = Encoding.UTF8.GetBytes(keyRef);
            var nonce = RandomNumberGenerator.GetBytes(FieldCipher.NonceSize);
            var cipher = new byte[dek.Length];
            var tag = new byte[FieldCipher.TagSize];
            using var aes = new AesGcm(rootKey, FieldCipher.TagSize);
            aes.Encrypt(nonce, dek, cipher, tag, aad);

            var wrapped = new byte[FieldCipher.NonceSize + FieldCipher.TagSize + cipher.Length];
            nonce.CopyTo(wrapped, 0);
            tag.CopyTo(wrapped, FieldCipher.NonceSize);
            cipher.CopyTo(wrapped, FieldCipher.NonceSize + FieldCipher.TagSize);
            return wrapped;
        }

        private static byte[] Unwrap(byte[] rootKey, byte[] wrapped, string keyRef)
        {
            if (wrapped.Length < FieldCipher.NonceSize + FieldCipher.TagSize)
                throw new CryptographicException($"Wrapped DEK for '{keyRef}' is corrupt (too short).");

            var aad = Encoding.UTF8.GetBytes(keyRef);
            var nonce = wrapped.AsSpan(0, FieldCipher.NonceSize);
            var tag = wrapped.AsSpan(FieldCipher.NonceSize, FieldCipher.TagSize);
            var cipher = wrapped.AsSpan(FieldCipher.NonceSize + FieldCipher.TagSize);
            var dek = new byte[cipher.Length];
            using var aes = new AesGcm(rootKey, FieldCipher.TagSize);
            aes.Decrypt(nonce, cipher, tag, dek, aad); // throws if the root key is wrong or the ref was tampered
            return dek;
        }
    }
}
