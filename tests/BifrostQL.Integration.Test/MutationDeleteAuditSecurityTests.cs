using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using GraphQL.Types;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Integration.Test;

/// <summary>
/// End-to-end coverage for the mutation write-path security fixes around delete
/// predicate contamination, soft-delete SET/WHERE separation, and the upsert
/// insert-branch. Drives real GraphQL mutations through
/// <see cref="DbTableMutateResolver"/> against a shared-cache in-memory SQLite
/// database, with the real <see cref="AuditMutationTransformer"/> and
/// <see cref="SoftDeleteMutationTransformer"/> wired into the pipeline.
/// </summary>
[Collection("MutationDeleteAuditSecurity")]
public sealed class MutationDeleteAuditSecurityTests : IAsyncLifetime
{
    private string _connectionString = null!;
    private SqliteConnection _keepAlive = null!;
    private SqliteDbConnFactory _connFactory = null!;
    private IDbModel _model = null!;
    private ISchema _schema = null!;

    // Widgets: soft-delete table (deleted_at) with an audit updated_at stamp.
    // Gadgets: hard-delete table with audit created_at + updated_at stamps.
    private static readonly string[] Metadata =
    {
        ":root { user-audit-key: id }",
        "main.Widgets { soft-delete: deleted_at }",
        "main.Widgets.updated_at { populate: updated-on }",
        "main.Gadgets.created_at { populate: created-on }",
        "main.Gadgets.updated_at { populate: updated-on }",
    };

    public async Task InitializeAsync()
    {
        _connectionString = $"Data Source=bifrost_delaudit_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(_connectionString);
        await _keepAlive.OpenAsync();

        _connFactory = new SqliteDbConnFactory(_connectionString);

        await using (var conn = new SqliteConnection(_connectionString))
        {
            await conn.OpenAsync();
            await new SqliteCommand(
                @"CREATE TABLE Widgets (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL,
                    status TEXT NOT NULL,
                    updated_at TEXT,
                    deleted_at TEXT
                );", conn).ExecuteNonQueryAsync();
            await new SqliteCommand(
                @"CREATE TABLE Gadgets (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL,
                    created_at TEXT,
                    updated_at TEXT
                );", conn).ExecuteNonQueryAsync();

            await new SqliteCommand(
                "INSERT INTO Widgets (name, status) VALUES ('one', 'active'), ('two', 'archived'), ('three', 'archived');",
                conn).ExecuteNonQueryAsync();
            await new SqliteCommand(
                "INSERT INTO Gadgets (name) VALUES ('g1');", conn).ExecuteNonQueryAsync();
        }

        var metadataLoader = new MetadataLoader(Metadata);
        var loader = new DbModelLoader(_connFactory, metadataLoader);
        _model = await loader.LoadAsync();
        _schema = DbSchema.FromModel(_model);
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task<ExecutionResult> ExecuteAsync(string query)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[]
            {
                new AuditMutationTransformer(),
                new SoftDeleteMutationTransformer(),
            },
        });
        services.AddSingleton<IFilterTransformers>(new FilterTransformersWrap
        {
            Transformers = Array.Empty<IFilterTransformer>(),
        });
        await using var provider = services.BuildServiceProvider();

        var executor = new SqlExecutionManager(_model, _schema);
        var extensions = new Dictionary<string, object?>
        {
            { "connFactory", _connFactory },
            { "model", _model },
            { "tableReaderFactory", executor },
        };

        return await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = _schema;
            options.Query = query;
            options.Extensions = new Inputs(extensions);
            options.RequestServices = provider;
            options.UserContext = new Dictionary<string, object?> { ["id"] = "user-1" };
        });
    }

    private (string status, bool deleted) GetWidget(long id)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        var cmd = new SqliteCommand("SELECT status, deleted_at FROM Widgets WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return (reader.GetString(0), !reader.IsDBNull(1));
    }

    private long GadgetCount()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return (long)new SqliteCommand("SELECT COUNT(*) FROM Gadgets", conn).ExecuteScalar()!;
    }

    private (bool exists, bool createdSet) GetGadget(long id)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        var cmd = new SqliteCommand("SELECT created_at FROM Gadgets WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return (false, false);
        return (true, !reader.IsDBNull(0));
    }

    // ---- Finding #4: soft-delete client predicate stays in WHERE, not SET ----

    [Fact]
    public async Task SoftDelete_ClientPredicate_DoesNotMatch_LeavesRowUntouched()
    {
        // Widget 1 has status 'active'; the delete predicate requires status
        // 'archived', so it must match zero rows — the row is neither soft-deleted
        // nor is its status overwritten to 'archived'.
        var result = await ExecuteAsync("mutation { widgets(delete: { id: 1, status: \"archived\" }) }");

        result.Errors.Should().BeNullOrEmpty();
        var (status, deleted) = GetWidget(1);
        deleted.Should().BeFalse("the predicate did not match so the row must not be soft-deleted");
        status.Should().Be("active", "the client predicate must remain a WHERE filter, never written into the row");
    }

    [Fact]
    public async Task SoftDelete_ClientPredicate_Matches_SoftDeletesWithoutOverwritingPredicateColumn()
    {
        // status 'active' matches Widget 1 → soft-deleted, but the status column
        // itself must NOT be assigned (it is a predicate, not a SET value).
        var result = await ExecuteAsync("mutation { widgets(delete: { id: 1, status: \"active\" }) }");

        result.Errors.Should().BeNullOrEmpty();
        var (status, deleted) = GetWidget(1);
        deleted.Should().BeTrue("the predicate matched so the row is soft-deleted");
        status.Should().Be("active", "the predicate column must not be turned into a SET assignment");
    }

    [Fact]
    public async Task SoftDelete_NoPrimaryKey_IsRejectedBySchema_NotMalformedSql()
    {
        // The generated delete input marks the primary key required, so a delete
        // with only a non-PK predicate is rejected at GraphQL validation — it never
        // reaches SQL generation. This is the contract that keeps the soft-delete
        // DELETE→UPDATE rewrite from ever emitting an empty/malformed WHERE. (The
        // resolver also rejects a predicate-less delete defensively, but the schema
        // stops it first.) No rows must change.
        var result = await ExecuteAsync("mutation { widgets(delete: { status: \"archived\" }) }");

        result.Errors.Should().NotBeNullOrEmpty();
        GetWidget(1).deleted.Should().BeFalse();
        GetWidget(2).deleted.Should().BeFalse();
        GetWidget(3).deleted.Should().BeFalse();
    }

    // ---- Finding #3: hard-delete WHERE not contaminated by audit stamps ----

    [Fact]
    public async Task HardDelete_WithAuditUpdatedStamp_ActuallyDeletesRow()
    {
        // Gadgets has an audit updated_at stamp that the transformer writes into the
        // delete data. That stamped column must not leak into the WHERE clause, or
        // WHERE Id=@Id AND updated_at=@now would match zero rows and silently no-op.
        var before = GadgetCount();

        var result = await ExecuteAsync("mutation { gadgets(delete: { id: 1 }) }");

        result.Errors.Should().BeNullOrEmpty();
        GadgetCount().Should().Be(before - 1, "the row must be physically deleted despite the audit stamp");
    }

    // ---- Finding #2: upsert insert-branch stamps created-* ----

    [Fact]
    public async Task Upsert_InsertBranch_StampsCreatedOn()
    {
        // Id 999 does not exist → the upsert must take the INSERT branch, which
        // stamps created_at. A native single-statement upsert-as-update would never
        // stamp created-on.
        var result = await ExecuteAsync("mutation { gadgets(upsert: { id: 999, name: \"fresh\" }) }");

        result.Errors.Should().BeNullOrEmpty();
        var (exists, createdSet) = GetGadget(999);
        exists.Should().BeTrue("a non-existent keyed upsert must insert the row");
        createdSet.Should().BeTrue("the insert branch must stamp created_at");
    }
}
