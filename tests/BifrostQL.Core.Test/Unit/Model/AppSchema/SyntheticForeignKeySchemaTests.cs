using System.Collections.Generic;
using System.Linq;
using BifrostQL.Core.Model;
using BifrostQL.Core.Model.AppSchema;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Model.AppSchema;

/// <summary>
/// A detector's synthetic FKs name tables by bare DbName. One Postgres database can hold
/// two WordPress sites in two schemas, each with its own <c>wp_posts</c> and
/// <c>wp_users</c>. Each site's FK must bind inside its own schema: never throw on the
/// shared name, never link one site's posts to the other site's users.
/// </summary>
public sealed class SyntheticForeignKeySchemaTests
{
    private static DbTable Table(string schema, string dbName)
    {
        var columns = new[]
        {
            new ColumnDto { ColumnName = "ID", GraphQlName = "ID", DataType = "int", OrdinalPosition = 1, IsPrimaryKey = true },
            new ColumnDto { ColumnName = "post_author", GraphQlName = "post_author", DataType = "int", OrdinalPosition = 2 },
        };
        return new DbTable
        {
            DbName = dbName,
            GraphQlName = $"{schema}_{dbName}",
            NormalizedName = dbName,
            TableSchema = schema,
            TableType = "BASE TABLE",
            ColumnLookup = columns.ToDictionary(c => c.ColumnName, System.StringComparer.OrdinalIgnoreCase),
            GraphQlLookup = columns.ToDictionary(c => c.GraphQlName, System.StringComparer.OrdinalIgnoreCase),
        };
    }

    private static AppSchemaResult PostsAuthorFk() => new(
        "wordpress",
        new List<PrefixGroup>(),
        new Dictionary<string, IDictionary<string, object?>>(),
        new[] { new SyntheticForeignKey("wp_posts", "post_author", "wp_users", "ID") });

    [Fact]
    public void SameNamedTablesInTwoSchemas_EachFkBindsInsideItsOwnSchema()
    {
        var model = new DbModel
        {
            Tables = new[]
            {
                Table("site_a", "wp_posts"), Table("site_a", "wp_users"),
                Table("site_b", "wp_posts"), Table("site_b", "wp_users"),
                // A third site with posts but no users: no parent in its schema, so no FK,
                // and in particular no FK reaching across into site_a or site_b.
                Table("site_c", "wp_posts"),
            },
            Metadata = new Dictionary<string, object?>(),
        };

        var fks = DbModel.BuildSyntheticForeignKeys(PostsAuthorFk(), model);

        fks.Select(fk => (fk.ChildTableSchema, fk.ChildTableName, fk.ParentTableSchema, fk.ParentTableName))
            .Should().BeEquivalentTo(new[]
            {
                ("site_a", "wp_posts", "site_a", "wp_users"),
                ("site_b", "wp_posts", "site_b", "wp_users"),
            });
    }

    [Fact]
    public void SingleSchema_BindsTheOneFk()
    {
        var model = new DbModel
        {
            Tables = new[] { Table("dbo", "wp_posts"), Table("dbo", "wp_users") },
            Metadata = new Dictionary<string, object?>(),
        };

        var fks = DbModel.BuildSyntheticForeignKeys(PostsAuthorFk(), model);

        fks.Should().ContainSingle();
        var fk = fks[0];
        (fk.ChildTableSchema, fk.ChildTableName, fk.ChildColumnNames.Single()).Should().Be(("dbo", "wp_posts", "post_author"));
        (fk.ParentTableSchema, fk.ParentTableName, fk.ParentColumnNames.Single()).Should().Be(("dbo", "wp_users", "ID"));
    }
}
