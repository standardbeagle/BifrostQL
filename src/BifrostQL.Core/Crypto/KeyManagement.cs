using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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
        ValueTask<byte[]?> LoadAsync(string keyRef, CancellationToken cancellationToken = default);

        /// <summary>Stores the wrapped DEK for <paramref name="keyRef"/>, overwriting any prior value.</summary>
        ValueTask StoreAsync(string keyRef, byte[] wrappedDek, CancellationToken cancellationToken = default);
    }

    /// <summary>Process-memory wrapped-DEK store. Thread-safe. Not durable across restarts.</summary>
    public sealed class InMemoryDataEncryptionKeyStore : IDataEncryptionKeyStore
    {
        private readonly ConcurrentDictionary<string, byte[]> _wrapped = new(StringComparer.Ordinal);

        public ValueTask<byte[]?> LoadAsync(string keyRef, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_wrapped.TryGetValue(keyRef, out var v) ? (byte[]?)v.Clone() : null);

        public ValueTask StoreAsync(string keyRef, byte[] wrappedDek, CancellationToken cancellationToken = default)
        {
            _wrapped[keyRef] = (byte[])wrappedDek.Clone();
            return ValueTask.CompletedTask;
        }
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
    /// <see cref="StoreAsync"/> is persist-if-absent / first-writer-wins: it writes to a unique
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
        private readonly SemaphoreSlim _sync = new(1, 1);

        public FileDataEncryptionKeyStore(string rootDirectory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
            _rootDir = rootDirectory;
            Directory.CreateDirectory(_rootDir);
        }

        public async ValueTask<byte[]?> LoadAsync(string keyRef, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(keyRef);
            var path = PathFor(keyRef);
            // First-writer-wins means the live file is only ever fully-written wrapped bytes,
            // and a key file is never deleted, so an existing path stays readable.
            return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : null;
        }

        public async ValueTask StoreAsync(string keyRef, byte[] wrappedDek, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(keyRef);
            ArgumentNullException.ThrowIfNull(wrappedDek);

            var path = PathFor(keyRef);
            // Cheap pre-check: if a value already exists, this is a no-op (first-writer-wins).
            // The authoritative guard is the fail-if-exists rename below (races/other processes).
            if (File.Exists(path))
                return;

            // Serialize same-process writers so two callers don't both pass the pre-check and
            // both attempt the rename; cross-process safety comes from the O_EXCL-style rename.
            await _sync.WaitAsync(cancellationToken);
            try
            {
                if (File.Exists(path))
                    return;

                var temp = Path.Combine(_rootDir, "." + Guid.NewGuid().ToString("N") + ".tmp");
                await File.WriteAllBytesAsync(temp, wrappedDek, cancellationToken);
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
            finally
            {
                _sync.Release();
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
        private readonly SemaphoreSlim _generateLock = new(1, 1);

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
        private const string VersionSeparator = "v";

        /// <summary>
        /// Returns the 32-byte plaintext DEK for <paramref name="keyRef"/>'s ORIGINAL
        /// (version 1 / unversioned) slot, unwrapping — or generating + wrapping on first
        /// use — as needed. This is the DEK legacy (unversioned-format) ciphertext was
        /// written under. The returned array is a copy the caller may use freely.
        /// </summary>
        public ValueTask<byte[]> GetDataKeyAsync(string keyRef, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(keyRef);
            return ResolveDekAsync(keyRef, cancellationToken);
        }

        /// <summary>
        /// Returns the 32-byte plaintext DEK for a SPECIFIC <paramref name="version"/> of
        /// <paramref name="keyRef"/> — the version-directed resolution a value written under an
        /// old DEK needs to decrypt after rotation. Version 1 is the original/unversioned slot;
        /// higher versions are created by <see cref="RotateAsync"/>.
        /// </summary>
        public ValueTask<byte[]> GetDataKeyAsync(string keyRef, int version, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(keyRef);
            if (version < 1)
                throw new ArgumentOutOfRangeException(nameof(version), version, "DEK version must be >= 1.");
            return ResolveDekAsync(StoreKeyFor(keyRef, version), cancellationToken);
        }

        /// <summary>
        /// Returns the current (highest) DEK version for <paramref name="keyRef"/> — the
        /// version new writes must encrypt under. Ensures version 1 exists (generating it on
        /// first use), then probes contiguous higher versions created by <see cref="RotateAsync"/>.
        /// </summary>
        public async ValueTask<int> GetCurrentVersionAsync(string keyRef, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(keyRef);
            // Ensure the base (v1) slot exists so a fresh key-ref reports version 1.
            await ResolveDekAsync(keyRef, cancellationToken);
            var version = 1;
            // Rotation always mints current+1, so persisted versions are contiguous: probe
            // upward until a slot is absent. A version already unwrapped in this process is
            // known to exist, so only the slot past it costs a store read.
            while (_dekCache.ContainsKey(StoreKeyFor(keyRef, version + 1))
                   || await _store.LoadAsync(StoreKeyFor(keyRef, version + 1), cancellationToken) is not null)
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
        public async ValueTask<int> RotateAsync(string keyRef, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(keyRef);
            var next = await GetCurrentVersionAsync(keyRef, cancellationToken) + 1;
            // Materialize (generate + persist) the new version's slot so GetCurrentVersionAsync
            // sees it as the new highest. Resolution is first-writer-wins, so a lost race adopts
            // the winner's DEK rather than orphaning data.
            await ResolveDekAsync(StoreKeyFor(keyRef, next), cancellationToken);
            return next;
        }

        /// <summary>
        /// Brings every DEK version of each key-ref into the in-process cache — version 1
        /// through the current one — so the synchronous per-row read paths
        /// (<see cref="TryGetCachedDataKey"/>, <see cref="GetCachedBlindIndexKey"/>) can run
        /// without I/O. Call it from the async edge of a request before projecting rows.
        /// </summary>
        public async ValueTask WarmAsync(IEnumerable<string> keyRefs, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(keyRefs);
            foreach (var keyRef in keyRefs.Distinct(StringComparer.Ordinal))
            {
                // A version newer than the cached ones exists only after a rotation, which this
                // process caches as it mints it. Another process's rotation is found by probing
                // the store for the next slot, at most once per VersionProbeInterval per key-ref,
                // so a read request costs no store I/O in the steady state.
                var now = Environment.TickCount64;
                if (_lastVersionProbe.TryGetValue(keyRef, out var probedAt)
                    && now - probedAt < VersionProbeInterval.TotalMilliseconds)
                    continue;

                var current = await GetCurrentVersionAsync(keyRef, cancellationToken);
                for (var version = 2; version <= current; version++)
                    await ResolveDekAsync(StoreKeyFor(keyRef, version), cancellationToken);
                _lastVersionProbe[keyRef] = now;
            }
        }

        /// <summary>
        /// How long <see cref="WarmAsync"/> trusts the cached versions of a key-ref before it
        /// probes the store again for a version another process minted. A value written under
        /// such a version reads as redacted until the next probe.
        /// </summary>
        internal static readonly TimeSpan VersionProbeInterval = TimeSpan.FromSeconds(1);

        private readonly ConcurrentDictionary<string, long> _lastVersionProbe = new(StringComparer.Ordinal);

        /// <summary>
        /// The cached DEK for <paramref name="keyRef"/> at <paramref name="version"/> (null =
        /// the original/unversioned slot), without I/O. False when <see cref="WarmAsync"/> has not
        /// brought that version in; the caller treats it as an undecryptable value.
        /// </summary>
        public bool TryGetCachedDataKey(string keyRef, int? version, out byte[] dek)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(keyRef);
            if (_dekCache.TryGetValue(StoreKeyFor(keyRef, version ?? 1), out var cached))
            {
                dek = (byte[])cached.Clone();
                return true;
            }
            dek = Array.Empty<byte>();
            return false;
        }

        private static string StoreKeyFor(string keyRef, int version)
            => version <= 1 ? keyRef : keyRef + VersionSeparator + version.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// Resolves (unwrapping, or generating + wrapping + persisting on first use) the DEK for
        /// a store key. The store key doubles as the wrap AAD, so a wrapped DEK cannot be
        /// relocated to another key-ref OR another version. Cached in memory per store key.
        /// </summary>
        private async ValueTask<byte[]> ResolveDekAsync(string storeKey, CancellationToken cancellationToken)
        {
            if (_dekCache.TryGetValue(storeKey, out var cached))
                return (byte[])cached.Clone();

            // Serialize the load-or-generate so two concurrent first-uses of the same
            // store key cannot each generate (and store) a different DEK — that would
            // orphan data encrypted under the loser's key.
            await _generateLock.WaitAsync(cancellationToken);
            try
            {
                if (_dekCache.TryGetValue(storeKey, out cached))
                    return (byte[])cached.Clone();

                var rootKey = _rootKeys.GetRootKey();
                var wrapped = await _store.LoadAsync(storeKey, cancellationToken);
                byte[] dek;
                if (wrapped is null)
                {
                    // Generate + persist. The store is persist-if-absent (first-writer-wins)
                    // for a durable/cross-process backend, so our store call may be a no-op if
                    // another manager already won the race. We therefore re-load and adopt
                    // the AUTHORITATIVE persisted bytes instead of trusting our own freshly
                    // generated DEK: otherwise the losing manager would cache and return a
                    // DEK that no writer persisted, orphaning anything it then encrypts.
                    // With the in-memory store (store overwrites) the re-load returns exactly
                    // what we just stored.
                    var justWrapped = Wrap(rootKey, RandomNumberGenerator.GetBytes(FieldCipher.KeySize), storeKey);
                    await _store.StoreAsync(storeKey, justWrapped, cancellationToken);
                    // A null here means the store failed to persist AND holds nothing — fail
                    // fast rather than fall back to our own un-persisted DEK, which would cache
                    // a key no writer stored and orphan everything encrypted under it on
                    // restart. (The durable store's contract is that a returned DEK is a
                    // persisted DEK.)
                    var authoritative = await _store.LoadAsync(storeKey, cancellationToken)
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
            finally
            {
                _generateLock.Release();
            }
        }

        /// <summary>
        /// Derives the per-column blind-index key from the ORIGINAL (version 1) DEK for
        /// <paramref name="keyRef"/> via HKDF-SHA-256, so the deterministic index key is distinct
        /// from the DEK that encrypts the data (compromising one does not reveal the other). Bound
        /// to version 1 deliberately: the blind-index hash must stay STABLE across key rotation so
        /// equality search still matches rows written under different DEK versions.
        ///
        /// <para><paramref name="columnBinding"/> — the stable length-prefixed
        /// (schema, table, column) triple from <see cref="CryptoAad.Build"/> — is folded into the
        /// HKDF <c>info</c> so two columns sharing a key-ref derive DISTINCT index keys. Without it,
        /// equal plaintext in different columns hashes identically: a cross-column equality oracle
        /// (correlate, or test a hidden column against a known value, purely from the visible index).
        /// The binding is column IDENTITY, never row data or DEK version, so the derived key stays
        /// constant across rotation — equality search still matches rows written under earlier DEKs.</para>
        /// </summary>
        public async ValueTask<byte[]> GetBlindIndexKeyAsync(string keyRef, ReadOnlyMemory<byte> columnBinding,
            CancellationToken cancellationToken = default)
            => DeriveBlindIndexKey(await GetDataKeyAsync(keyRef, cancellationToken), columnBinding.Span);

        /// <summary>
        /// <see cref="GetBlindIndexKeyAsync"/> from the cache, for the synchronous query rewrite.
        /// Version 1 never changes once persisted, so one <see cref="WarmAsync"/> per manager
        /// suffices. Throws when the key-ref was never warmed: no token can be derived.
        /// </summary>
        public byte[] GetCachedBlindIndexKey(string keyRef, ReadOnlySpan<byte> columnBinding)
        {
            if (!TryGetCachedDataKey(keyRef, null, out var dek))
                throw new InvalidOperationException(
                    $"The data-encryption key for '{keyRef}' was not loaded before the query rewrite; " +
                    "warm the key manager on the request's async path first.");
            return DeriveBlindIndexKey(dek, columnBinding);
        }

        private static byte[] DeriveBlindIndexKey(byte[] dek, ReadOnlySpan<byte> columnBinding)
        {
            var info = new byte[BlindIndexInfo.Length + columnBinding.Length];
            BlindIndexInfo.CopyTo(info, 0);
            columnBinding.CopyTo(info.AsSpan(BlindIndexInfo.Length));
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, dek, outputLength: 32, info: info);
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
