using System.Data.Common;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;

namespace BifrostQL.Integration.Test;

/// <summary>
/// Real violations from every supported engine, classified by
/// <see cref="BifrostExecutionError.IsUniqueViolation"/> and
/// <see cref="BifrostExecutionError.FromDatabaseException"/>. A unique or primary-key violation is a
/// conflict; a foreign-key, not-null or check violation is not; and an error whose text merely
/// carries a uniqueness phrase (an echoed value, a trigger's own message) is not either.
///
/// <para>What the pinned drivers report, measured on the test containers:</para>
/// <list type="bullet">
/// <item>MySqlConnector: SqlState <c>23000</c> for duplicate key, foreign key and not-null alike;
/// only <c>Data["Server Error Code"]</c> = 1062 marks a duplicate key.</item>
/// <item>Npgsql: SqlState <c>23505</c>.</item>
/// <item>Microsoft.Data.SqlClient: no SqlState; <c>Data["HelpLink.EvtID"]</c> = 2627 (constraint) or
/// 2601 (unique index).</item>
/// <item>Microsoft.Data.Sqlite: no SqlState and nothing in <c>Data</c>; the extended code
/// (2067 unique, 1555 primary key) is only on <c>SqliteException.SqliteExtendedErrorCode</c>. A
/// trigger's RAISE text gets the same "SQLite Error 19: '...'" wrapper, so the text cannot tell
/// them apart.</item>
/// </list>
/// </summary>
public sealed class ConstraintViolationClassificationTests
{
    private static readonly string Suffix = Guid.NewGuid().ToString("N")[..8];
    private static readonly string Parent = $"uv_p_{Suffix}";
    private static readonly string Child = $"uv_c_{Suffix}";

    private static string[] Ddl(string textType) =>
    [
        $"CREATE TABLE {Parent} (id INT PRIMARY KEY, code {textType} NOT NULL UNIQUE, n INT NOT NULL CHECK (n >= 0), tag VARCHAR(20) NULL)",
        $"CREATE UNIQUE INDEX ux_{Suffix} ON {Parent} (tag)",
        $"CREATE TABLE {Child} (id INT PRIMARY KEY, p_id INT NOT NULL, FOREIGN KEY (p_id) REFERENCES {Parent}(id))",
        $"INSERT INTO {Parent} (id, code, n, tag) VALUES (1, 'a', 1, 't')",
    ];

    private static readonly string PrimaryKeyDuplicate = $"INSERT INTO {Parent} (id, code, n) VALUES (1, 'b', 1)";
    private static readonly string UniqueConstraintDuplicate = $"INSERT INTO {Parent} (id, code, n) VALUES (2, 'a', 1)";
    private static readonly string UniqueIndexDuplicate = $"INSERT INTO {Parent} (id, code, n, tag) VALUES (3, 'c', 1, 't')";
    private static readonly string ForeignKeyMissing = $"INSERT INTO {Child} (id, p_id) VALUES (1, 99)";
    private static readonly string NotNullMissing = $"INSERT INTO {Parent} (id, code, n) VALUES (4, NULL, 1)";
    private static readonly string CheckFailed = $"INSERT INTO {Parent} (id, code, n) VALUES (5, 'e', -1)";

    private static async Task<DbException> Fail(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var act = async () => await command.ExecuteNonQueryAsync();
        return (await act.Should().ThrowAsync<DbException>()).Which;
    }

    private static async Task Exec(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task WithSchema(DbConnection connection, string textType, Func<Task> body)
    {
        await connection.OpenAsync();
        foreach (var statement in Ddl(textType))
            await Exec(connection, statement);
        try
        {
            await body();
        }
        finally
        {
            if (connection is not SqliteConnection)
            {
                await Exec(connection, $"DROP TABLE {Child}");
                await Exec(connection, $"DROP TABLE {Parent}");
            }
        }
    }

    private static async Task AssertKinds(DbConnection connection)
    {
        foreach (var sql in new[] { PrimaryKeyDuplicate, UniqueConstraintDuplicate, UniqueIndexDuplicate })
        {
            var error = await Fail(connection, sql);
            BifrostExecutionError.IsUniqueViolation(error).Should().BeTrue(sql);
            BifrostExecutionError.FromDatabaseException(error).ErrorCode.Should().Be(BifrostExecutionError.ConflictCode, sql);
        }

        foreach (var sql in new[] { ForeignKeyMissing, NotNullMissing, CheckFailed })
        {
            var error = await Fail(connection, sql);
            BifrostExecutionError.IsUniqueViolation(error).Should().BeFalse(sql);
            BifrostExecutionError.FromDatabaseException(error).ErrorCode.Should().NotBe(BifrostExecutionError.ConflictCode, sql);
        }
    }

    [SkippableFact]
    public async Task MySql_duplicate_key_is_a_conflict_and_a_value_echoing_the_phrase_is_not()
    {
        var connectionString = Environment.GetEnvironmentVariable("BIFROST_TEST_MYSQL");
        Skip.If(connectionString is null, "BIFROST_TEST_MYSQL environment variable not set");
        await using var connection = new MySqlConnection(connectionString);
        await WithSchema(connection, "VARCHAR(20)", async () =>
        {
            await AssertKinds(connection);

            // Error 1366 echoes the rejected value: "Incorrect integer value: 'Duplicate entry' ...".
            var echo = await Fail(connection, $"INSERT INTO {Parent} (id, code, n) VALUES (6, 'f', 'Duplicate entry')");
            BifrostExecutionError.IsUniqueViolation(echo).Should().BeFalse("the phrase is the caller's value, not a duplicate key");
        });
    }

    [SkippableFact]
    public async Task Postgres_unique_violation_is_a_conflict_and_a_value_echoing_the_phrase_is_invalid()
    {
        var connectionString = Environment.GetEnvironmentVariable("BIFROST_TEST_POSTGRES");
        Skip.If(connectionString is null, "BIFROST_TEST_POSTGRES environment variable not set");
        await using var connection = new NpgsqlConnection(connectionString);
        await WithSchema(connection, "VARCHAR(20)", async () =>
        {
            await AssertKinds(connection);

            // 22P02 echoes the rejected value: invalid input syntax for type integer: "duplicate key".
            var echo = await Fail(connection, $"INSERT INTO {Parent} (id, code, n) VALUES (6, 'f', 'duplicate key')");
            BifrostExecutionError.IsUniqueViolation(echo).Should().BeFalse("the phrase is the caller's value, not a duplicate key");
            BifrostExecutionError.FromDatabaseException(echo).ErrorCode.Should().Be(BifrostExecutionError.InvalidValueCode);
        });
    }

    [SkippableFact]
    public async Task SqlServer_duplicate_key_is_a_conflict_and_a_value_echoing_the_phrase_is_not()
    {
        var connectionString = Environment.GetEnvironmentVariable("BIFROST_TEST_SQLSERVER");
        Skip.If(connectionString is null, "BIFROST_TEST_SQLSERVER environment variable not set");
        await using var connection = new SqlConnection(connectionString);
        await WithSchema(connection, "VARCHAR(20)", async () =>
        {
            await AssertKinds(connection);

            // Error 2628 echoes the value cut to the column width: "... Truncated value: 'duplicate key at the'".
            var echo = await Fail(connection, $"INSERT INTO {Parent} (id, code, n) VALUES (6, 'duplicate key at the front', 1)");
            BifrostExecutionError.IsUniqueViolation(echo).Should().BeFalse("the phrase is the caller's value, not a duplicate key");
        });
    }

    [Fact]
    public async Task Sqlite_unique_violation_is_a_conflict_and_a_trigger_message_with_the_phrase_is_not()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await Exec(connection, "PRAGMA foreign_keys = ON");
        await WithSchema(connection, "TEXT", async () =>
        {
            await AssertKinds(connection);

            // A trigger's RAISE text is the application's, reported as SQLITE_CONSTRAINT_TRIGGER.
            await Exec(connection, $"CREATE TRIGGER uv_t_{Suffix} BEFORE INSERT ON {Parent} WHEN NEW.id = 6 BEGIN SELECT RAISE(ABORT, 'UNIQUE constraint failed: by policy'); END");
            var echo = await Fail(connection, $"INSERT INTO {Parent} (id, code, n) VALUES (6, 'f', 1)");
            BifrostExecutionError.IsUniqueViolation(echo).Should().BeFalse("the phrase is a trigger's message, not the engine's unique check");
        });
    }
}
