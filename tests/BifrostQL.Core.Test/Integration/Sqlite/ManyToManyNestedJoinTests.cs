using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using GraphQL.SystemTextJson;
using Microsoft.Data.Sqlite;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// A relationship nested beneath a many-to-many collection cannot be correlated
/// through the junction: the parent m2m restricted sub-query exposes SOURCE-key
/// values as its JoinIds, but the nested stitch matches them using the m2m's
/// ConnectedColumns, which name TARGET columns — and the junction bridge is never
/// consulted. That silently produced wrong/empty rows. It must now fail loudly
/// with a clear error rather than return incorrect data (full junction-aware
/// correlation is a deferred feature).
/// </summary>
public sealed class ManyToManyNestedJoinTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_m2m_nested_join_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("PRAGMA foreign_keys = ON");
        await Exec("DROP TABLE IF EXISTS book_authors");
        await Exec("DROP TABLE IF EXISTS books");
        await Exec("DROP TABLE IF EXISTS authors");
        await Exec("DROP TABLE IF EXISTS countries");
        await Exec(
            """
            CREATE TABLE countries (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL
            )
            """);
        await Exec(
            """
            CREATE TABLE authors (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                country_id INTEGER NOT NULL,
                FOREIGN KEY (country_id) REFERENCES countries(id)
            )
            """);
        await Exec(
            """
            CREATE TABLE books (
                id INTEGER PRIMARY KEY,
                title TEXT NOT NULL
            )
            """);
        // Pure junction -> auto-detected as many-to-many between books and authors.
        await Exec(
            """
            CREATE TABLE book_authors (
                book_id INTEGER NOT NULL,
                author_id INTEGER NOT NULL,
                PRIMARY KEY (book_id, author_id),
                FOREIGN KEY (book_id) REFERENCES books(id),
                FOREIGN KEY (author_id) REFERENCES authors(id)
            )
            """);
        await Exec("INSERT INTO countries(id, name) VALUES (1, 'US')");
        await Exec("INSERT INTO authors(id, name, country_id) VALUES (1, 'a1', 1)");
        await Exec("INSERT INTO books(id, title) VALUES (1, 'b1')");
        await Exec("INSERT INTO book_authors(book_id, author_id) VALUES (1, 1)");

        var factory = new SqliteDbConnFactory(ConnString);
        var loader = new DbModelLoader(factory, new MetadataLoader(Array.Empty<string>()));
        _model = await loader.LoadAsync();
    }

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    [Fact]
    public async Task NestedRelationshipBeneathManyToMany_FailsWithClearError()
    {
        // Sanity: the junction was detected as a many-to-many bridge.
        _model.GetTableFromDbName("books").ManyToManyLinks.Should().ContainKey("authors");

        var schema = DbSchema.FromModel(_model);
        var executor = new DocumentExecuter();
        var factory = new SqliteDbConnFactory(ConnString);
        var execution = await executor.ExecuteAsync(options =>
        {
            options.Schema = schema;
            // `countries` is a single-link on authors, nested beneath the m2m
            // `authors` collection on books.
            options.Query =
                """
                {
                  books {
                    data {
                      id
                      authors {
                        data {
                          id
                          countries { id name }
                        }
                      }
                    }
                  }
                }
                """;
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = factory,
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema),
            });
        });

        execution.Errors.Should().NotBeNullOrEmpty();
        var allMessages = string.Join(" | ", execution.Errors!.Select(e => $"{e.Message} :: {e.InnerException?.Message}"));
        allMessages.Should().Contain("many-to-many", $"actual errors: {allMessages}");
    }
}
