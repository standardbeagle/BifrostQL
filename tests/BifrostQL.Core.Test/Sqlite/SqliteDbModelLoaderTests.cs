using BifrostQL.Core.Model;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// Integration tests for loading SQLite schemas through DbModelLoader.
/// Uses in-memory databases to verify the end-to-end schema loading pipeline:
/// SqliteSchemaReader -> DbModel.FromTables -> IDbModel with links.
/// </summary>
public sealed class SqliteDbModelLoaderTests : IAsyncLifetime
{
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;
    private const string ConnString = "Data Source=bifrost_loader_test;Mode=Memory;Cache=Shared";

    public async Task InitializeAsync()
    {
        // Shared cache keeps the DB alive across connections
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await CreateSchemaAsync(_keepAlive);

        var factory = new SqliteDbConnFactory(ConnString);
        var metadataLoader = new MetadataLoader(Array.Empty<string>());
        var loader = new DbModelLoader(factory, metadataLoader);
        _model = await loader.LoadAsync();
    }

    public async Task DisposeAsync()
    {
        await _keepAlive.DisposeAsync();
    }

    private static async Task CreateSchemaAsync(SqliteConnection conn)
    {
        var statements = new[]
        {
            @"CREATE TABLE Authors (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Country TEXT
            )",
            @"CREATE TABLE Genres (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Description TEXT
            )",
            @"CREATE TABLE Books (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                AuthorId INTEGER NOT NULL,
                GenreId INTEGER NOT NULL,
                Title TEXT NOT NULL,
                Price REAL NOT NULL,
                InStock INTEGER NOT NULL DEFAULT 1,
                PublishedDate TEXT,
                FOREIGN KEY (AuthorId) REFERENCES Authors(Id),
                FOREIGN KEY (GenreId) REFERENCES Genres(Id)
            )",
            @"CREATE TABLE Reviews (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                BookId INTEGER NOT NULL,
                Rating INTEGER NOT NULL,
                Comment TEXT,
                FOREIGN KEY (BookId) REFERENCES Books(Id)
            )"
        };

        foreach (var sql in statements)
        {
            await using var cmd = new SqliteCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    #region Model Loading

    [Fact]
    public void Model_IsNotNull()
    {
        _model.Should().NotBeNull();
    }

    [Fact]
    public void Model_ContainsAllTables()
    {
        var names = _model.Tables.Select(t => t.DbName).ToList();
        names.Should().Contain("Authors");
        names.Should().Contain("Genres");
        names.Should().Contain("Books");
        names.Should().Contain("Reviews");
    }

    [Fact]
    public void Model_TableCount()
    {
        _model.Tables.Should().HaveCount(4);
    }

    #endregion

    #region Table Lookup

    [Fact]
    public void GetTableFromDbName_ReturnsCorrectTable()
    {
        var table = _model.GetTableFromDbName("Authors");
        table.DbName.Should().Be("Authors");
    }

    [Fact]
    public void GetTableByFullGraphQlName_ReturnsCorrectTable()
    {
        var authorsTable = _model.Tables.First(t => t.DbName == "Authors");
        var table = _model.GetTableByFullGraphQlName(authorsTable.GraphQlName);
        table.DbName.Should().Be("Authors");
    }

    #endregion

    #region Column Properties

    [Fact]
    public void Authors_IdColumn_IsIdentityAndPrimaryKey()
    {
        var table = _model.GetTableFromDbName("Authors");
        var id = table.ColumnLookup["Id"];

        id.IsIdentity.Should().BeTrue();
        id.IsPrimaryKey.Should().BeTrue();
        id.IsNullable.Should().BeFalse();
    }

    [Fact]
    public void Books_HasAllColumns()
    {
        var table = _model.GetTableFromDbName("Books");
        table.ColumnLookup.Should().ContainKey("Id");
        table.ColumnLookup.Should().ContainKey("AuthorId");
        table.ColumnLookup.Should().ContainKey("GenreId");
        table.ColumnLookup.Should().ContainKey("Title");
        table.ColumnLookup.Should().ContainKey("Price");
        table.ColumnLookup.Should().ContainKey("InStock");
        table.ColumnLookup.Should().ContainKey("PublishedDate");
    }

    [Fact]
    public void Books_NullableColumn_IsNullable()
    {
        var table = _model.GetTableFromDbName("Books");
        table.ColumnLookup["PublishedDate"].IsNullable.Should().BeTrue();
    }

    [Fact]
    public void Books_NonNullableColumn_IsNotNullable()
    {
        var table = _model.GetTableFromDbName("Books");
        table.ColumnLookup["Title"].IsNullable.Should().BeFalse();
    }

    #endregion

    #region Join / Link Discovery

    [Fact]
    public void Books_HasSingleLinkToAuthors()
    {
        var books = _model.GetTableFromDbName("Books");

        books.SingleLinks.Should().ContainKey("authors",
            "Books.AuthorId FK should create an 'authors' single link");
    }

    [Fact]
    public void Books_HasSingleLinkToGenres()
    {
        var books = _model.GetTableFromDbName("Books");

        books.SingleLinks.Should().ContainKey("genres",
            "Books.GenreId FK should create a 'genres' single link");
    }

    [Fact]
    public void Authors_HasMultiLinkToBooks()
    {
        var authors = _model.GetTableFromDbName("Authors");

        authors.MultiLinks.Should().ContainKey("books",
            "Authors should have a 'books' multi link via AuthorId FK");
    }

    [Fact]
    public void Genres_HasMultiLinkToBooks()
    {
        var genres = _model.GetTableFromDbName("Genres");

        genres.MultiLinks.Should().ContainKey("books",
            "Genres should have a 'books' multi link via GenreId FK");
    }

    [Fact]
    public void Reviews_HasSingleLinkToBooks()
    {
        var reviews = _model.GetTableFromDbName("Reviews");

        reviews.SingleLinks.Should().ContainKey("books",
            "Reviews.BookId FK should create a 'books' single link");
    }

    [Fact]
    public void Books_HasMultiLinkToReviews()
    {
        var books = _model.GetTableFromDbName("Books");

        books.MultiLinks.Should().ContainKey("reviews",
            "Books should have a 'reviews' multi link via BookId FK");
    }

    [Fact]
    public void SingleLink_PointsToCorrectParentTable()
    {
        var books = _model.GetTableFromDbName("Books");
        var link = books.SingleLinks["authors"];

        link.ParentTable.DbName.Should().Be("Authors");
        link.ChildTable.DbName.Should().Be("Books");
        link.ChildId.ColumnName.Should().Be("AuthorId");
        link.ParentId.ColumnName.Should().Be("Id");
    }

    [Fact]
    public void MultiLink_PointsToCorrectChildTable()
    {
        var authors = _model.GetTableFromDbName("Authors");
        var link = authors.MultiLinks["books"];

        link.ParentTable.DbName.Should().Be("Authors");
        link.ChildTable.DbName.Should().Be("Books");
    }

    #endregion

    #region Schema Metadata

    [Fact]
    public void AllTables_HaveMainSchema()
    {
        foreach (var table in _model.Tables)
        {
            table.TableSchema.Should().Be("main");
        }
    }

    [Fact]
    public void AllTables_HaveBaseTableType()
    {
        foreach (var table in _model.Tables)
        {
            ((DbTable)table).TableType.Should().Be("BASE TABLE");
        }
    }

    #endregion
}
