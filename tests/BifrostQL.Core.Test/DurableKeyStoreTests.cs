using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BifrostQL.Core.Crypto;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// Coverage for durable DEK persistence: the manager re-Load convergence fix and the
/// file-backed <see cref="FileDataEncryptionKeyStore"/> (persist-if-absent, at-rest
/// wrapped-only, crash-safe atomic first-writer-wins). These pin the security property
/// that concurrent first-uses of a key-ref across processes converge on ONE DEK, so no
/// caller ever encrypts data under a DEK that will be orphaned by another writer.
/// </summary>
public class DurableKeyStoreTests
{
    private static byte[] Key(byte seed)
    {
        var k = new byte[FieldCipher.KeySize];
        Array.Fill(k, seed);
        return k;
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bifrost-dek-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// A store that simulates a LOST persist-if-absent race: the first Load sees an empty
    /// store (so the manager generates), Store is a no-op (a prior writer already won), and
    /// the re-Load returns the pre-seeded winner. Without the manager re-Load-and-adopt fix
    /// the manager would cache and return its own freshly generated DEK (the orphaning bug).
    /// </summary>
    private sealed class LostRaceStore : IDataEncryptionKeyStore
    {
        private readonly byte[] _winner;
        private bool _firstLoadDone;

        public LostRaceStore(byte[] winner) => _winner = winner;

        public byte[]? Load(string keyRef)
        {
            if (!_firstLoadDone) { _firstLoadDone = true; return null; }
            return (byte[])_winner.Clone();
        }

        // persist-if-absent: the winner is already persisted, so this is a no-op.
        public void Store(string keyRef, byte[] wrappedDek) { }
    }

    [Fact]
    public void GetDataKey_LostStoreRace_AdoptsPersistedWinnerDek()
    {
        var root = Key(7);
        const string keyRef = "config:pii";

        // Produce a real wrapped winner DEK via a manager over an in-memory store.
        var winnerStore = new InMemoryDataEncryptionKeyStore();
        var winnerDek = new EnvelopeKeyManager(new ConfigRootKeyProvider(root), winnerStore).GetDataKey(keyRef);
        var winnerWrapped = winnerStore.Load(keyRef)!;

        // A manager that loses the persist race must still return the persisted winner DEK.
        var loser = new EnvelopeKeyManager(new ConfigRootKeyProvider(root), new LostRaceStore(winnerWrapped));
        var loserDek = loser.GetDataKey(keyRef);

        loserDek.Should().Equal(winnerDek);
    }

    [Fact]
    public void FileStore_PersistsWrappedBytesVerbatim_PlaintextDekNeverAtRest()
    {
        var dir = TempDir();
        try
        {
            var root = Key(3);
            const string keyRef = "config:pii";

            // Wrap a known plaintext DEK using a manager over an in-memory store, then
            // persist those exact wrapped bytes via the file store.
            var stage = new InMemoryDataEncryptionKeyStore();
            var plaintextDek = new EnvelopeKeyManager(new ConfigRootKeyProvider(root), stage).GetDataKey(keyRef);
            var wrapped = stage.Load(keyRef)!;

            var store = new FileDataEncryptionKeyStore(dir);
            store.Store(keyRef, wrapped);

            // Exactly one file was written and it holds the wrapped bytes verbatim.
            var files = Directory.GetFiles(dir);
            files.Should().HaveCount(1);
            var atRest = File.ReadAllBytes(files[0]);
            atRest.Should().Equal(wrapped);

            // The plaintext DEK must never appear at rest.
            Contains(atRest, plaintextDek).Should().BeFalse("the plaintext DEK must not exist at rest");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FileStore_SurvivesRestart_FreshInstanceLoadsAndUnwraps()
    {
        var dir = TempDir();
        try
        {
            var root = Key(4);
            const string keyRef = "config:pii";

            var stage = new InMemoryDataEncryptionKeyStore();
            var originalDek = new EnvelopeKeyManager(new ConfigRootKeyProvider(root), stage).GetDataKey(keyRef);
            var wrapped = stage.Load(keyRef)!;

            new FileDataEncryptionKeyStore(dir).Store(keyRef, wrapped);

            // A FRESH store over the SAME directory (simulated restart) returns the same bytes.
            var reopened = new FileDataEncryptionKeyStore(dir);
            reopened.Load(keyRef).Should().Equal(wrapped);

            // And a manager over the reopened store unwraps to the original plaintext DEK.
            var dek = new EnvelopeKeyManager(new ConfigRootKeyProvider(root), reopened).GetDataKey(keyRef);
            dek.Should().Equal(originalDek);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FileStore_Load_ReturnsNull_WhenAbsent()
    {
        var dir = TempDir();
        try
        {
            new FileDataEncryptionKeyStore(dir).Load("config:missing").Should().BeNull();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FileStore_Store_IsFirstWriterWins_DoesNotClobber()
    {
        var dir = TempDir();
        try
        {
            const string keyRef = "config:pii";
            var wrappedA = MakeWrappedBytes(0xA1);
            var wrappedB = MakeWrappedBytes(0xB2);

            var store = new FileDataEncryptionKeyStore(dir);
            store.Store(keyRef, wrappedA);
            store.Store(keyRef, wrappedB); // must NOT replace wrappedA

            store.Load(keyRef).Should().Equal(wrappedA);
            Directory.GetFiles(dir).Should().HaveCount(1, "no temp file should be left behind");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FileStore_ArbitraryKeyRef_CannotEscapeDirectory_OrCollide()
    {
        var dir = TempDir();
        try
        {
            var store = new FileDataEncryptionKeyStore(dir);
            var a = MakeWrappedBytes(0x11);
            var b = MakeWrappedBytes(0x22);

            // Path-hostile key-refs with dots/slashes must stay inside the directory and
            // must not collide with each other.
            store.Store("../escape:pii", a);
            store.Store("config:../../etc/passwd", b);

            Directory.GetFiles(dir).Should().HaveCount(2);
            store.Load("../escape:pii").Should().Equal(a);
            store.Load("config:../../etc/passwd").Should().Equal(b);
            // Nothing was written outside the directory.
            Directory.GetFiles(dir).All(f => Path.GetDirectoryName(f) == dir.TrimEnd(Path.DirectorySeparatorChar)
                                             || Path.GetFullPath(Path.GetDirectoryName(f)!) == Path.GetFullPath(dir))
                     .Should().BeTrue();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task ConcurrentGetDataKey_AcrossManagers_OneStore_ConvergeOnOneDek()
    {
        var dir = TempDir();
        try
        {
            var root = Key(5);
            const string keyRef = "config:pii";
            const int n = 16;

            // ONE durable store shared by N independent managers (each its own cache + lock).
            var store = new FileDataEncryptionKeyStore(dir);

            var tasks = Enumerable.Range(0, n).Select(_ => Task.Run(() =>
                new EnvelopeKeyManager(new ConfigRootKeyProvider(root), store).GetDataKey(keyRef))).ToArray();
            var deks = await Task.WhenAll(tasks);

            // Every returned DEK is byte-identical: the persist-if-absent store picks one
            // winner and the manager re-Load fix makes every loser adopt it.
            foreach (var dek in deks)
                dek.Should().Equal(deks[0]);

            // Exactly one wrapped DEK is persisted.
            Directory.GetFiles(dir).Should().HaveCount(1);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // A well-formed wrapped-DEK-shaped byte blob ([nonce:12][tag:16][ciphertext:32]) for
    // no-clobber / encoding tests that don't need real cryptographic content.
    private static byte[] MakeWrappedBytes(byte seed)
    {
        var b = new byte[FieldCipher.NonceSize + FieldCipher.TagSize + FieldCipher.KeySize];
        Array.Fill(b, seed);
        return b;
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length) return false;
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { match = false; break; }
            if (match) return true;
        }
        return false;
    }
}
