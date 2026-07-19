using System;
using System.Collections.Concurrent;
using System.IO;
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
    /// Durable file-backed wrapped-DEK store: one file per key-ref under a root directory,
    /// holding the root-key-wrapped DEK bytes verbatim (never plaintext). Opt-in — the DI
    /// wiring never fabricates this; a deployment registers it explicitly.
    /// <para>
    /// Filenames are the lowercase-hex encoding of the UTF-8 key-ref, so arbitrary key-ref
    /// strings (dots, slashes, <c>..</c>) can neither escape the directory nor collide — the
    /// raw key-ref is never used as a path segment.
    /// </para>
    /// <para>
    /// <see cref="Store"/> is persist-if-absent / first-writer-wins: it writes to a unique
    /// temp file in the same directory then atomically renames it into place with
    /// fail-if-exists semantics. If the destination already exists (another writer/process
    /// won), the temp file is deleted and the existing value is left intact — overwriting
    /// would orphan every value already encrypted under the persisted DEK. A half-written
    /// temp file is never the live file, so readers never see a torn DEK.
    /// </para>
    /// </summary>
    public sealed class FileDataEncryptionKeyStore : IDataEncryptionKeyStore
    {
        private readonly string _rootDir;
        private readonly object _sync = new();

        public FileDataEncryptionKeyStore(string rootDirectory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
            _rootDir = rootDirectory;
            Directory.CreateDirectory(_rootDir);
        }

        public byte[]? Load(string keyRef)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(keyRef);
            var path = PathFor(keyRef);
            // First-writer-wins means the live file is only ever fully-written wrapped bytes.
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        public void Store(string keyRef, byte[] wrappedDek)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(keyRef);
            ArgumentNullException.ThrowIfNull(wrappedDek);

            var path = PathFor(keyRef);
            // Cheap pre-check: if a value already exists, this is a no-op (first-writer-wins).
            // The authoritative guard is the fail-if-exists rename below (races/other processes).
            if (File.Exists(path))
                return;

            // Serialize same-process writers so two threads don't both pass the pre-check and
            // both attempt the rename; cross-process safety comes from the O_EXCL-style rename.
            lock (_sync)
            {
                if (File.Exists(path))
                    return;

                var temp = Path.Combine(_rootDir, "." + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllBytes(temp, wrappedDek);
                try
                {
                    // Atomic, fail-if-destination-exists — the cross-process first-writer-wins gate.
                    File.Move(temp, path, overwrite: false);
                }
                catch (IOException)
                {
                    // File.Move(overwrite:false) throws IOException BOTH when another writer
                    // won the race (destination already exists — a benign no-op) AND on a
                    // genuine move failure (disk full, permissions, IO error) that persisted
                    // nothing. Distinguish them: only a destination that now EXISTS confirms a
                    // lost race we may swallow. If nothing is there, persistence genuinely
                    // failed — rethrow so the caller does not proceed to cache an unpersisted
                    // DEK (which would make encrypted data unrecoverable on restart).
                    TryDelete(temp);
                    if (!File.Exists(path))
                        throw;
                }
                catch
                {
                    TryDelete(temp);
                    throw;
                }
            }
        }

        private string PathFor(string keyRef)
            => Path.Combine(_rootDir, Convert.ToHexString(Encoding.UTF8.GetBytes(keyRef)).ToLowerInvariant());

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* best-effort cleanup of an orphaned temp file */ }
        }
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
        /// The DEK-version separator embedded in a store key. Version 1 uses the bare key-ref
        /// (backward compatible with pre-rotation stored DEKs); version N&gt;1 appends this
        /// unit-separator + <c>v</c> + N, so versioned slots never collide with the base slot
        /// and the durable file store hex-encodes the whole thing safely.
        /// </summary>
        private const string VersionSeparator = "v";

        /// <summary>
        /// Returns the 32-byte plaintext DEK for <paramref name="keyRef"/>'s ORIGINAL
        /// (version 1 / unversioned) slot, unwrapping — or generating + wrapping on first
        /// use — as needed. This is the DEK legacy (unversioned-format) ciphertext was
        /// written under. The returned array is a copy the caller may use freely.
        /// </summary>
        public byte[] GetDataKey(string keyRef)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(keyRef);
            return ResolveDek(keyRef);
        }

        /// <summary>
        /// Returns the 32-byte plaintext DEK for a SPECIFIC <paramref name="version"/> of
        /// <paramref name="keyRef"/> — the version-directed resolution the read path uses so
        /// a value written under an old DEK still decrypts after rotation. Version 1 is the
        /// original/unversioned slot; higher versions are created by <see cref="Rotate"/>.
        /// </summary>
        public byte[] GetDataKey(string keyRef, int version)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(keyRef);
            if (version < 1)
                throw new ArgumentOutOfRangeException(nameof(version), version, "DEK version must be >= 1.");
            return ResolveDek(StoreKeyFor(keyRef, version));
        }

        /// <summary>
        /// Returns the current (highest) DEK version for <paramref name="keyRef"/> — the
        /// version new writes must encrypt under. Ensures version 1 exists (generating it on
        /// first use), then probes contiguous higher versions created by <see cref="Rotate"/>.
        /// </summary>
        public int GetCurrentVersion(string keyRef)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(keyRef);
            // Ensure the base (v1) slot exists so a fresh key-ref reports version 1.
            ResolveDek(keyRef);
            var version = 1;
            // Rotation always mints current+1, so persisted versions are contiguous: probe
            // upward until a slot is absent. Versions are few (one per rotation), so this is cheap.
            while (_store.Load(StoreKeyFor(keyRef, version + 1)) is not null)
                version++;
            return version;
        }

        /// <summary>
        /// Rotates <paramref name="keyRef"/>: generates a fresh DEK as a NEW higher version and
        /// makes it the current version for writes, WITHOUT touching any prior version — every
        /// old DEK stays durably resolvable so values already written under it still decrypt.
        /// Returns the new current version. Idempotent under a concurrent race: two rotations
        /// computing the same next version converge on one persisted DEK (first-writer-wins).
        /// </summary>
        public int Rotate(string keyRef)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(keyRef);
            lock (_generateLock)
            {
                var next = GetCurrentVersion(keyRef) + 1;
                // Materialize (generate + persist) the new version's slot so GetCurrentVersion
                // sees it as the new highest. ResolveDek is first-writer-wins, so a lost race
                // adopts the winner's DEK rather than orphaning data.
                ResolveDek(StoreKeyFor(keyRef, next));
                return next;
            }
        }

        private static string StoreKeyFor(string keyRef, int version)
            => version <= 1 ? keyRef : keyRef + VersionSeparator + version.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// Resolves (unwrapping, or generating + wrapping + persisting on first use) the DEK for
        /// a store key. The store key doubles as the wrap AAD, so a wrapped DEK cannot be
        /// relocated to another key-ref OR another version. Cached in memory per store key.
        /// </summary>
        private byte[] ResolveDek(string storeKey)
        {
            if (_dekCache.TryGetValue(storeKey, out var cached))
                return (byte[])cached.Clone();

            // Serialize the load-or-generate so two concurrent first-uses of the same
            // store key cannot each generate (and store) a different DEK — that would
            // orphan data encrypted under the loser's key.
            lock (_generateLock)
            {
                if (_dekCache.TryGetValue(storeKey, out cached))
                    return (byte[])cached.Clone();

                var rootKey = _rootKeys.GetRootKey();
                var wrapped = _store.Load(storeKey);
                byte[] dek;
                if (wrapped is null)
                {
                    // Generate + persist. The store is persist-if-absent (first-writer-wins)
                    // for a durable/cross-process backend, so our Store may be a no-op if
                    // another manager already won the race. We therefore re-Load and adopt
                    // the AUTHORITATIVE persisted bytes instead of trusting our own freshly
                    // generated DEK: otherwise the losing manager would cache and return a
                    // DEK that no writer persisted, orphaning anything it then encrypts.
                    // With the in-memory store (Store overwrites) the re-Load returns exactly
                    // what we just stored, so behavior is identical and no test regresses.
                    var justWrapped = Wrap(rootKey, RandomNumberGenerator.GetBytes(FieldCipher.KeySize), storeKey);
                    _store.Store(storeKey, justWrapped);
                    // After a successful Store (or a lost race the store swallowed only because
                    // the winner's value is now persisted), Load MUST return the authoritative
                    // bytes. A null here means the store failed to persist AND holds nothing —
                    // fail fast rather than fall back to our own un-persisted DEK, which would
                    // cache a key no writer stored and orphan everything encrypted under it on
                    // restart. (No silent fallback data — the durable store's contract is that
                    // a returned DEK is a persisted DEK.)
                    var authoritative = _store.Load(storeKey)
                        ?? throw new CryptographicException(
                            $"Data-encryption key for '{storeKey}' was not persisted by the key store; " +
                            "refusing to use an unpersisted key that would make encrypted data unrecoverable.");
                    dek = Unwrap(rootKey, authoritative, storeKey);
                }
                else
                {
                    dek = Unwrap(rootKey, wrapped, storeKey);
                }

                _dekCache[storeKey] = dek;
                return (byte[])dek.Clone();
            }
        }

        /// <summary>
        /// Derives the per-column blind-index key from the ORIGINAL (version 1) DEK for
        /// <paramref name="keyRef"/> via HKDF-SHA-256, so the deterministic index key is distinct
        /// from the DEK that encrypts the data (compromising one does not reveal the other). Bound
        /// to version 1 deliberately: the blind-index hash must stay STABLE across key rotation so
        /// equality search still matches rows written under different DEK versions.
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
