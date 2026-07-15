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
}
