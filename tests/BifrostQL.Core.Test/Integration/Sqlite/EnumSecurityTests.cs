using BifrostQL.Core.Model;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// Integration test for soft-delete scoping of enum lookup tables over SQLite
/// (in-memory). Enum membership is baked into the schema at build time and is
/// therefore context-free, but soft-delete is itself a context-free predicate
/// (<c>{col} IS NULL</c>): soft-deleted lookup rows must NOT become enum members.
/// </summary>
public sealed class EnumSecurityTests : IAsyncLifetime
{
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;
    private DbModelLoader _loader = null!;
    private const string ConnString = "Data Source=bifrost_enum_security_test;Mode=Memory;Cache=Shared";

    // Marks "status" as an enum (value column "code") AND soft-delete on "deleted_at".
    private const string Rule = "*.status { enum: code; soft-delete: deleted_at }";

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await CreateSchemaAsync(_keepAlive);

        var factory = new SqliteDbConnFactory(ConnString);
        _loader = new DbModelLoader(factory, new MetadataLoader(new[] { Rule }));
        _model = await _loader.LoadAsync();
    }

    public async Task DisposeAsync()
    {
        await _keepAlive.DisposeAsync();
    }

    private static async Task CreateSchemaAsync(SqliteConnection conn)
    {
        var statements = new[]
        {
            "DROP TABLE IF EXISTS status",
            "CREATE TABLE status (id INTEGER PRIMARY KEY, code TEXT, deleted_at TEXT)",
            "INSERT INTO status(code, deleted_at) VALUES " +
                "('active', NULL),('inactive', NULL),('archived', '2024-01-01')",
        };

        foreach (var sql in statements)
        {
            await using var cmd = new SqliteCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task LoadEnumValuesAsync_ExcludesSoftDeletedRows_FromEnumMembership()
    {
        var result = await _loader.LoadEnumValuesAsync(_model);

        result.Values.Should().ContainKey("status");
        var names = result.Values["status"].Select(e => e.GraphQlName).ToList();

        names.Should().Contain("ACTIVE");
        names.Should().Contain("INACTIVE");
        names.Should().NotContain("ARCHIVED", "soft-deleted lookup rows must not become enum members");
    }
}
