using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using GraphQL.SystemTextJson;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// A filter can TRAVERSE a relationship — <c>comments(filter: { posts: { title:
/// {_eq: "x"} } })</c>, where <c>post</c> names a SingleLinks relationship rather
/// than a column. That renders an INNER JOIN against a sub-query over the PARENT
/// table. These tests pin that the parent's own row-scoping transformers
/// (tenant-filter, soft-delete) constrain that sub-query.
///
/// Without them the traversal is an inference channel: a caller who cannot see a
/// parent row can still match children through it, so the parent's existence and
/// its field values leak through the child result set — a caller can binary-search
/// another tenant's post title by which comments come back. This is the row-filter
/// half of the traversal that already has its column-guard half covered (the
/// filter-column collector recurses into relationship sub-filters and asserts each
/// column against ITS OWN table's policy); row scoping did not recurse with it.
///
/// The fixture deliberately leaves the CHILD table unscoped and scopes only the
/// PARENT. Every pre-existing relationship-filter test builds tables with no
/// tenant/soft-delete/policy metadata at all, so none of them could observe
/// whether the sub-query was scoped.
/// </summary>
public sealed class RelationshipFilterParentScopeTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_relfilter_parent_scope_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("PRAGMA foreign_keys = ON");
        await Exec("DROP TABLE IF EXISTS comments");
        await Exec("DROP TABLE IF EXISTS posts");
        await Exec(
            """
            CREATE TABLE posts (
                id INTEGER PRIMARY KEY,
                tenant_id INTEGER NOT NULL,
                title TEXT NOT NULL,
                deleted_at TEXT
            )
            """);
        // The child carries NO row scoping of its own — the only thing that may
        // keep tenant 1 from reaching tenant 2's data is the parent's filter.
        await Exec(
            """
            CREATE TABLE comments (
                id INTEGER PRIMARY KEY,
                post_id INTEGER NOT NULL,
                body TEXT NOT NULL,
                FOREIGN KEY (post_id) REFERENCES posts(id)
            )
            """);
        await Exec(
            """
            INSERT INTO posts(id, tenant_id, title, deleted_at) VALUES
                (1, 1, 'shared-title', NULL),
                (2, 2, 'shared-title', NULL),
                (3, 1, 'retracted', '2026-01-01T00:00:00Z')
            """);
        await Exec(
            """
            INSERT INTO comments(id, post_id, body) VALUES
                (10, 1, 'on tenant-one post'),
                (20, 2, 'on tenant-two post'),
                (30, 3, 'on retracted post')
            """);

        var factory = new SqliteDbConnFactory(ConnString);
        var loader = new DbModelLoader(factory, new MetadataLoader(new[]
        {
            "*.posts { tenant-filter: tenant_id; soft-delete: deleted_at }",
        }));
        _model = await loader.LoadAsync();
    }

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    /// <summary>
    /// Both tenants own a post titled "shared-title". Tenant 1 filtering comments
    /// through the post relationship must reach only its own post's comments; the
    /// other tenant's comment must not surface.
    /// </summary>
    [Fact]
    public async Task RelationshipFilter_ScopesTraversedParentToCallersTenant()
    {
        var ids = await ExecuteCommentIdsAsync(
            """comments(filter: { posts: { title: {_eq: "shared-title"} } }, sort: [id_asc])""",
            tenantId: 1);

        ids.Should().Equal(new[] { 10 },
            "the traversed posts sub-query must carry the posts table's own tenant filter");
    }

    [Fact]
    public async Task RelationshipFilter_ScopesTraversedParentToCallersTenant_OtherDirection()
    {
        var ids = await ExecuteCommentIdsAsync(
            """comments(filter: { posts: { title: {_eq: "shared-title"} } }, sort: [id_asc])""",
            tenantId: 2);

        ids.Should().Equal(new[] { 20 });
    }

    /// <summary>
    /// A soft-deleted parent must not be reachable through a filter traversal
    /// either — otherwise the caller can confirm a retracted row still exists and
    /// read its field values by probing.
    /// </summary>
    [Fact]
    public async Task RelationshipFilter_ExcludesSoftDeletedParent()
    {
        var ids = await ExecuteCommentIdsAsync(
            """comments(filter: { posts: { title: {_eq: "retracted"} } }, sort: [id_asc])""",
            tenantId: 1);

        ids.Should().BeEmpty(
            "the traversed posts sub-query must carry the posts table's soft-delete filter");
    }

    /// <summary>
    /// The parent's filter must not be double-applied to the CHILD, and an ordinary
    /// in-scope traversal must still return its rows.
    /// </summary>
    [Fact]
    public async Task RelationshipFilter_InScopeParent_StillReturnsChildren()
    {
        var ids = await ExecuteCommentIdsAsync(
            """comments(filter: { posts: { id: {_eq: 1} } }, sort: [id_asc])""",
            tenantId: 1);

        ids.Should().Equal(new[] { 10 });
    }

    /// <summary>
    /// A query with NO relationship traversal must be unaffected: the child has no
    /// scoping metadata, so every comment is visible. Guards against the fix
    /// leaking the parent's predicate onto the outer table.
    /// </summary>
    [Fact]
    public async Task PlainChildQuery_IsUnaffectedByTheParentsScope()
    {
        var ids = await ExecuteCommentIdsAsync("comments(sort: [id_asc])", tenantId: 1);

        ids.Should().Equal(new[] { 10, 20, 30 });
    }

    private async Task<List<int>> ExecuteCommentIdsAsync(string commentsField, int tenantId)
    {
        var schema = DbSchema.FromModel(_model);
        var factory = new SqliteDbConnFactory(ConnString);
        var filterTransformers = new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[]
            {
                new TenantFilterTransformer(),
                new SoftDeleteFilterTransformer(),
            },
        };
        var execManager = new SqlExecutionManager(_model, schema, new QueryTransformerService(filterTransformers));

        var executor = new DocumentExecuter();
        var execution = await executor.ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query =
                $$"""
                {
                  {{commentsField}} {
                    data { id body }
                  }
                }
                """;
            options.UserContext = new Dictionary<string, object?> { ["tenant_id"] = tenantId };
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = factory,
                ["model"] = _model,
                ["tableReaderFactory"] = execManager,
            });
        });

        execution.Errors.Should().BeNullOrEmpty();
        var json = new GraphQLSerializer().Serialize(execution);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement
            .GetProperty("data")
            .GetProperty("comments")
            .GetProperty("data")
            .EnumerateArray()
            .Select(p => p.GetProperty("id").GetInt32())
            .ToList();
    }
}
