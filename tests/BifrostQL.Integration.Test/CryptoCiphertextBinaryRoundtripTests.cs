using System.Data.Common;
using BifrostQL.Core.Crypto;
using BifrostQL.Integration.Test.Infrastructure;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Integration.Test;

/// <summary>
/// Proves that an AES-256-GCM ciphertext envelope round-trips losslessly through EACH dialect's
/// native binary column type — SQL Server <c>VARBINARY</c>, Postgres <c>BYTEA</c>, MySQL
/// <c>VARBINARY</c>, SQLite <c>BLOB</c> — and is still decryptable after the round-trip. The
/// envelope is opaque binary (a random nonce/tag/ciphertext, plus the embedded key version for
/// rotation), so any dialect that silently re-encoded, truncated, or normalized the bytes would
/// corrupt every stored secret. SQLite always runs; the container-backed dialects skip when their
/// connection-string env var is unset.
/// </summary>
public sealed class CryptoCiphertextBinaryRoundtripTests
{
    private const string Plaintext = "123-45-6789";

    public static IEnumerable<object[]> Dialects()
    {
        yield return new object[] { "SQLite", "BLOB", (Func<IIntegrationTestDatabase>)(() => new SqliteTestDatabase()) };
        yield return new object[] { "SQL Server", "VARBINARY(MAX)", (Func<IIntegrationTestDatabase>)(() => new SqlServerTestDatabase()) };
        yield return new object[] { "Postgres", "BYTEA", (Func<IIntegrationTestDatabase>)(() => new PostgresTestDatabase()) };
        yield return new object[] { "MySQL", "VARBINARY(1024)", (Func<IIntegrationTestDatabase>)(() => new MySqlTestDatabase()) };
    }

    [SkippableTheory]
    [MemberData(nameof(Dialects))]
    public async Task Ciphertext_RoundTripsThroughBinaryColumn_AndStillDecrypts(
        string provider, string binaryColumnType, Func<IIntegrationTestDatabase> makeDb)
    {
        var db = makeDb();
        try
        {
            await db.InitializeAsync();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("environment variable not set"))
        {
            throw new SkipException($"{provider} is not available: {ex.Message}");
        }

        try
        {
            // A versioned ciphertext envelope (format 2 with an embedded key version) as raw bytes.
            var dek = new byte[FieldCipher.KeySize];
            for (var i = 0; i < dek.Length; i++) dek[i] = (byte)(i + 1);
            var aad = CryptoAad.Build("dbo", "secrets", "ssn");
            var envelope = FieldCipher.Encrypt(dek, Plaintext, aad, keyVersion: 3);
            var ciphertextBytes = Convert.FromBase64String(envelope);

            using var conn = db.ConnFactory.GetConnection();
            await conn.OpenAsync();

            await ExecAsync(conn, $"CREATE TABLE crypto_bin (id INT NOT NULL PRIMARY KEY, payload {binaryColumnType} NULL)");

            await using (var insert = conn.CreateCommand())
            {
                insert.CommandText = "INSERT INTO crypto_bin (id, payload) VALUES (1, @p)";
                var p = insert.CreateParameter();
                p.ParameterName = "@p";
                p.Value = ciphertextBytes; // bound as the provider's native binary type
                insert.Parameters.Add(p);
                await insert.ExecuteNonQueryAsync();
            }

            byte[] readBack;
            await using (var select = conn.CreateCommand())
            {
                select.CommandText = "SELECT payload FROM crypto_bin WHERE id = 1";
                readBack = (byte[])(await select.ExecuteScalarAsync())!;
            }

            readBack.Should().Equal(ciphertextBytes, $"{provider} must round-trip the ciphertext bytes verbatim");
            FieldCipher.PeekKeyVersion(Convert.ToBase64String(readBack))
                .Should().Be(3, "the embedded key version survives the binary round-trip");
            FieldCipher.Decrypt(dek, Convert.ToBase64String(readBack), aad)
                .Should().Be(Plaintext, "the round-tripped ciphertext still decrypts");
        }
        finally
        {
            await db.DisposeAsync();
        }
    }

    private static async Task ExecAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
