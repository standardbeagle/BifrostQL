using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using RootExecutionNode = GraphQL.Execution.RootExecutionNode;

namespace BifrostQL.Integration.Test.FullIntegration;

/// <summary>
/// Regression for the "same table reached via two join paths nulls the deeper
/// one" bug. When a single query reaches one table (here <c>users</c>) through
/// two different parents — board → client → user (the lead) and board →
/// deliverable → user (the owner) — both single-links carry the same join field
/// name (<c>users</c>). The result reader resolved nested join fields against
/// the ROOT query's flattened <c>RecurseJoins</c> matched by name only, so
/// <c>FirstOrDefault(Name == "users")</c> always returned the first path and the
/// second path read the wrong (or empty) result set, surfacing as null.
/// The reader bug is in result assembly (post-SQL), so it is dialect
/// independent; SQLite exercises the full pipeline.
/// </summary>
[Collection("SqliteDualPathSameTable")]
public class SqliteDualPathSameTableTests : FullIntegrationTestBase, IAsyncLifetime
{
    private SqliteConnection? _keepAliveConnection;

    public async Task InitializeAsync()
    {
        var connectionString = "Data Source=bifrost_dualpath_test;Mode=Memory;Cache=Shared";
        _keepAliveConnection = new SqliteConnection(connectionString);
        await _keepAliveConnection.OpenAsync();

        var factory = new SqliteDbConnFactory(connectionString);
        await base.InitializeAsync(factory, CreateSchemaAsync, SeedDataAsync);
    }

    public async Task DisposeAsync()
    {
        await base.CleanupAsync();
        if (_keepAliveConnection != null)
            await _keepAliveConnection.DisposeAsync();
    }

    private static async Task CreateSchemaAsync(System.Data.Common.DbConnection conn)
    {
        var statements = new[]
        {
            "DROP TABLE IF EXISTS deliverables",
            "DROP TABLE IF EXISTS boards",
            "DROP TABLE IF EXISTS clients",
            "DROP TABLE IF EXISTS users",
            @"CREATE TABLE users (
                user_id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL
            )",
            @"CREATE TABLE clients (
                client_id INTEGER PRIMARY KEY AUTOINCREMENT,
                lead_id INTEGER NOT NULL,
                name TEXT NOT NULL,
                FOREIGN KEY (lead_id) REFERENCES users(user_id)
            )",
            @"CREATE TABLE boards (
                board_id INTEGER PRIMARY KEY AUTOINCREMENT,
                client_id INTEGER NOT NULL,
                name TEXT NOT NULL,
                FOREIGN KEY (client_id) REFERENCES clients(client_id)
            )",
            @"CREATE TABLE deliverables (
                deliverable_id INTEGER PRIMARY KEY AUTOINCREMENT,
                board_id INTEGER NOT NULL,
                owner_id INTEGER NOT NULL,
                name TEXT NOT NULL,
                FOREIGN KEY (board_id) REFERENCES boards(board_id),
                FOREIGN KEY (owner_id) REFERENCES users(user_id)
            )"
        };

        foreach (var sql in statements)
        {
            var cmd = new SqliteCommand(sql, (SqliteConnection)conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task SeedDataAsync(System.Data.Common.DbConnection conn)
    {
        var statements = new[]
        {
            // Distinct users so a wrong-path read is observable, not a coincidental match.
            "INSERT INTO users (name) VALUES ('Lead Larry'), ('Owner Olivia')",
            "INSERT INTO clients (lead_id, name) VALUES (1, 'Acme')",   // client.lead -> Lead Larry
            "INSERT INTO boards (client_id, name) VALUES (1, 'Q3 Board')",
            "INSERT INTO deliverables (board_id, owner_id, name) VALUES (1, 2, 'Spec')" // deliverable.owner -> Owner Olivia
        };

        foreach (var sql in statements)
        {
            var cmd = new SqliteCommand(sql, (SqliteConnection)conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static List<Dictionary<string, JsonElement>> ExtractPagedData(ExecutionResult result, string tableName)
    {
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();
        var root = (Dictionary<string, object?>)((RootExecutionNode)result.Data!).ToValue()!;
        root.Should().ContainKey(tableName);
        var json = JsonSerializer.Serialize(root[tableName]);
        var paged = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        var dataJson = paged["data"].GetRawText();
        return JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(dataJson)!;
    }

    private static string Str(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null! : e.ToString();

    [Fact]
    public async Task Query_SameTableViaTwoPaths_BothResolveToOwnRow()
    {
        // board -> client -> users (the lead)  AND  board -> deliverables -> users (the owner)
        // Both single-links to `users` share the join field name "users".
        var query = @"query {
            boards {
                data {
                    name
                    clients {
                        name
                        users { user_id name }
                    }
                    deliverables {
                        data {
                            name
                            users { user_id name }
                        }
                    }
                }
            }
        }";

        var rows = ExtractPagedData(await ExecuteQueryAsync(query), "boards");
        rows.Should().HaveCount(1);

        var board = rows[0];

        // Path 1: board -> client -> lead user
        var client = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(board["clients"].GetRawText())!;
        var leadUser = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(client["users"].GetRawText())!;
        Str(leadUser["name"]).Should().Be("Lead Larry");

        // Path 2 (the deeper sibling): board -> deliverable -> owner user
        var deliverables = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(
            board["deliverables"].GetProperty("data").GetRawText())!;
        deliverables.Should().HaveCount(1);
        var ownerWrapper = deliverables[0]["users"];
        // The bug surfaced here: ownerWrapper was null because the nested "users"
        // field resolved to the client->users join, not the deliverable->users join.
        ownerWrapper.ValueKind.Should().NotBe(JsonValueKind.Null, "deliverable.owner user must resolve via its own join path");
        var ownerUser = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ownerWrapper.GetRawText())!;
        Str(ownerUser["name"]).Should().Be("Owner Olivia");
    }
}
