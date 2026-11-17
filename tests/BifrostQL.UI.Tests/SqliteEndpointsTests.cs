using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.UI.Web;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.UI.Tests;

/// <summary>
/// SQLite file connect from the form or a recent connection. It used to call only
/// a connection test, so the header showed the file while the server kept serving
/// the previous database (or nothing). <c>/api/sqlite/connect</c> must BIND the
/// file, never create one unless asked, and build the string itself (quoted).
/// </summary>
public sealed class SqliteEndpointsTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bifrost-sqlite-ep-{Guid.NewGuid():N}");
    private readonly ConnectionState _state = new();
    private WebApplication? _app;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.MapSqliteEndpoints(_state);
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.Single()) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Post(string path, object payload)
    {
        var response = await _client.PostAsync(path,
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    private string ExistingDb(string name)
    {
        var path = Path.Combine(_dir, name);
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY)";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        return path;
    }

    [Fact]
    public async Task Connect_BindsTheFileAsTheActiveConnection()
    {
        var path = ExistingDb("app.db");

        var (status, body) = await Post("/api/sqlite/connect", new { filePath = path });

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("database").GetString().Should().Be("app.db");
        var active = _state.Active;
        active.Should().NotBeNull();
        active!.Provider.Should().Be(BifrostDbProvider.Sqlite);
        new SqliteConnectionStringBuilder(active.ConnectionString).DataSource.Should().Be(path);
    }

    [Fact]
    public async Task Connect_RecentConnectionString_BindsToo()
    {
        var path = ExistingDb("recent.db");

        var (status, _) = await Post("/api/sqlite/connect", new { connectionString = $"Data Source={path}" });

        status.Should().Be(HttpStatusCode.OK);
        new SqliteConnectionStringBuilder(_state.Active!.ConnectionString).DataSource.Should().Be(path);
    }

    [Fact]
    public async Task Connect_MissingFileWithoutCreateNew_FailsAndCreatesNothing()
    {
        var path = Path.Combine(_dir, "typo.db");

        var (status, _) = await Post("/api/sqlite/connect", new { filePath = path });

        status.Should().Be(HttpStatusCode.BadRequest);
        File.Exists(path).Should().BeFalse();
        _state.Active.Should().BeNull();
    }

    [Fact]
    public async Task Connect_CreateNew_CreatesAndBinds()
    {
        var path = Path.Combine(_dir, "fresh.db");

        var (status, _) = await Post("/api/sqlite/connect", new { filePath = path, createNew = true });

        status.Should().Be(HttpStatusCode.OK);
        File.Exists(path).Should().BeTrue();
        _state.Active.Should().NotBeNull();
    }

    [Fact]
    public async Task Test_MissingFileWithoutCreateNew_FailsAndCreatesNothing()
    {
        var path = Path.Combine(_dir, "typo.db");

        var (status, _) = await Post("/api/sqlite/test", new { filePath = path });

        status.Should().Be(HttpStatusCode.BadRequest);
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task Test_CreateNew_ReportsWithoutCreating()
    {
        var path = Path.Combine(_dir, "later.db");

        var (status, body) = await Post("/api/sqlite/test", new { filePath = path, createNew = true });

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("message").GetString().Should().Contain("will be created");
        File.Exists(path).Should().BeFalse();
        _state.Active.Should().BeNull();
    }

    [Fact]
    public void BuildConnectionString_QuotesAPathThatLooksLikeKeywords()
    {
        // Unquoted, "Data Source=<this>" would parse as a second Mode keyword.
        var path = Path.Combine(_dir, "a;Mode=Memory.db");

        var built = SqliteEndpoints.BuildConnectionString(new SqliteConnectRequest(FilePath: path));

        var parsed = new SqliteConnectionStringBuilder(built);
        parsed.DataSource.Should().Be(path);
        parsed.Mode.Should().Be(SqliteOpenMode.ReadWrite);
    }

    [Fact]
    public void BuildConnectionString_RawStringKeepsAnExplicitMode()
    {
        var built = SqliteEndpoints.BuildConnectionString(new SqliteConnectRequest(ConnectionString: "Data Source=x.db;Mode=ReadOnly"));

        new SqliteConnectionStringBuilder(built).Mode.Should().Be(SqliteOpenMode.ReadOnly);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("/a.db", "Data Source=/b.db")]
    public void BuildConnectionString_RequiresExactlyOneSource(string? filePath, string? connectionString)
    {
        var act = () => SqliteEndpoints.BuildConnectionString(new SqliteConnectRequest(filePath, connectionString));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildConnectionString_RejectsANonSqliteString()
    {
        var act = () => SqliteEndpoints.BuildConnectionString(new SqliteConnectRequest(ConnectionString: "Server=db;User Id=sa"));

        act.Should().Throw<ArgumentException>();
    }
}
