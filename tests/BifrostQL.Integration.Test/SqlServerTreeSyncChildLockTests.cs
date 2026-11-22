using BifrostQL.Integration.Test.Infrastructure;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Model;
using BifrostQL.MySql;
using BifrostQL.Ngsql;
using BifrostQL.SqlServer;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace BifrostQL.Integration.Test;

/// <summary>
/// On SQL Server a tree sync's child read must keep a NEW child of a synced parent out until
/// the sync commits. <c>UPDLOCK</c> alone locks only the rows read, and a concurrent child
/// INSERT's foreign-key check takes a shared lock on the parent, which an update lock does not
/// block, so the insert landed between the sync's diff and its commit. The child read now
/// takes <c>WITH (UPDLOCK, HOLDLOCK)</c>: key-range locks over the range the read scanned.
/// Two connections: the loader reads inside an open transaction on one, a writer with a short
/// <c>LOCK_TIMEOUT</c> inserts on the other.
/// </summary>
public sealed class SqlServerTreeSyncChildLockTests : IAsyncLifetime
{
    private const string SkipReason = "BIFROST_TEST_SQLSERVER environment variable not set";

    /// <summary>SQL Server error 1222: lock request time out period exceeded.</summary>
    private const int LockRequestTimeout = 1222;

    private string? _master;
    private string? _dbName;
    private string _connectionString = "";
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _master = Environment.GetEnvironmentVariable("BIFROST_TEST_SQLSERVER");
        if (_master is null) return;
        _dbName = $"bifrost_sync_lock_{Guid.NewGuid():N}";
        await ExecAsync(_master, $"CREATE DATABASE [{_dbName}]");
        _connectionString = new SqlConnectionStringBuilder(_master) { InitialCatalog = _dbName }.ConnectionString;
        await ExecAsync(_connectionString,
            """
            CREATE TABLE blogs (id INT IDENTITY PRIMARY KEY, name NVARCHAR(100) NOT NULL);
            CREATE TABLE posts (id INT IDENTITY PRIMARY KEY, blog_id INT NOT NULL REFERENCES blogs(id), title NVARCHAR(100) NOT NULL);
            CREATE INDEX ix_posts_blog_id ON posts(blog_id);
            INSERT INTO blogs (name) VALUES ('synced'), ('other');
            INSERT INTO posts (blog_id, title) VALUES (1, 'keep'), (2, 'other blog');
            """);
        _model = await new DbModelLoader(new SqlServerDbConnFactory(_connectionString), new MetadataLoader(Array.Empty<string>())).LoadAsync();
    }

    public async Task DisposeAsync()
    {
        if (_master is null || _dbName is null) return;
        await TestDatabaseDrop.SqlServerAsync(_master, _dbName);
    }

    [SkippableFact]
    public async Task A_concurrent_child_insert_waits_until_the_sync_commits()
    {
        Skip.If(_master is null, SkipReason);

        var blogs = _model.GetTableFromDbName("blogs");
        var linkKey = blogs.MultiLinks.Keys.Should().ContainSingle().Subject;
        var tree = new Dictionary<string, object?>
        {
            ["id"] = 1,
            ["name"] = "synced",
            [linkKey] = new List<object?> { new Dictionary<string, object?> { ["id"] = 1, ["title"] = "keep" } },
        };
        var loader = new TreeSyncStateLoader(SqlServerDialect.Instance, _model, new Dictionary<string, object?>());

        await using var sync = new SqlConnection(_connectionString);
        await sync.OpenAsync();
        await ExecAsync(sync, SqlServerDialect.Instance.BeginTransactionSql);
        var existing = await loader.LoadAsync(blogs, tree, sync);
        existing.Should().NotBeNull();
        ((List<Dictionary<string, object?>>)existing![linkKey]!).Should().ContainSingle("the loaded child set is the one post");

        var blocked = await TryInsertPostAsync(blogId: 1);
        blocked.Should().NotBeNull("a new child of the synced parent waits on the child read's key-range lock");
        blocked!.Number.Should().Be(LockRequestTimeout);

        // Control: the same insert, once the sync has committed, lands.
        await ExecAsync(sync, SqlServerDialect.Instance.CommitTransactionSql);
        (await TryInsertPostAsync(blogId: 1)).Should().BeNull("the sync's commit releases the range");
    }

    /// <summary>
    /// The HOLDLOCK is SQL Server's child read only: its root read keeps <c>UPDLOCK</c>, and
    /// PostgreSQL and MySQL lock children exactly as before (their trailing FOR UPDATE).
    /// </summary>
    [Fact]
    public void Only_the_SQL_Server_child_read_adds_HOLDLOCK()
    {
        SqlServerDialect.Instance.ChildSetLockTableHint.Should().Be(" WITH (UPDLOCK, HOLDLOCK)");
        SqlServerDialect.Instance.UpdateLockTableHint.Should().Be(" WITH (UPDLOCK)");
        PostgresDialect.Instance.ChildSetLockTableHint.Should().Be(PostgresDialect.Instance.UpdateLockTableHint).And.BeEmpty();
        MySqlDialect.Instance.ChildSetLockTableHint.Should().Be(MySqlDialect.Instance.UpdateLockTableHint).And.BeEmpty();
    }

    // Inserts a post under blogId on its own connection with a 1 s lock timeout: null when it
    // committed, the SqlException when it could not.
    private async Task<SqlException?> TryInsertPostAsync(int blogId)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        try
        {
            await ExecAsync(conn, $"SET LOCK_TIMEOUT 1000; INSERT INTO posts (blog_id, title) VALUES ({blogId}, 'concurrent');");
            return null;
        }
        catch (SqlException ex)
        {
            return ex;
        }
    }

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await ExecAsync(conn, sql);
    }

    private static async Task ExecAsync(SqlConnection conn, string sql)
    {
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
