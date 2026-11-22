using BifrostQL.Core.Model;
using BifrostQL.Core.Model.Relationships;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Test.Model.Robustness;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Model;

/// <summary>
/// The junction named in <c>many-to-many: Target:Junction</c> metadata is a bare DbName, but a
/// table's bare name is not its identity. The pre-fix lookup took the first table with that
/// DbName in read order, so with <c>archive.user_roles</c> read before
/// <c>sales.user_roles</c>, <c>sales.users</c>' entry resolved to archive's junction, found no
/// foreign keys to sales, and failed model load. A bare junction name now resolves in the
/// source table's schema first, then only to a name defined in exactly one schema; a
/// <c>schema.name</c> form names one exactly; a bare name in two or more other schemas is
/// reported as ambiguous.
/// </summary>
public sealed class ManyToManyJunctionSchemaTests
{
    private static DbTable Table(string schema, string name, Action<DbModelTestFixture.TableBuilder> configure)
    {
        var builder = new DbModelTestFixture.TableBuilder(name)
            .WithSchema(schema)
            .WithGraphQlName($"{schema}_{name}");
        configure(builder);
        return builder.Build();
    }

    // A payload column keeps auto-detection from bridging on its own, so only the metadata
    // entry can produce the link under test.
    private static DbTable Junction(string schema) => Table(schema, "user_roles", t => t
        .WithPrimaryKey("id").WithColumn("user_id", "int").WithColumn("role_id", "int")
        .WithColumn("granted_by", "nvarchar"));

    private static IEnumerable<DbForeignKey> JunctionKeys(string schema) => new[]
    {
        Fk($"FK_{schema}_ur_users", schema, "user_id", schema, "users"),
        Fk($"FK_{schema}_ur_roles", schema, "role_id", schema, "roles"),
    };

    private static DbForeignKey Fk(string name, string childSchema, string childColumn, string parentSchema, string parentTable) => new()
    {
        ConstraintName = name,
        ChildTableSchema = childSchema,
        ChildTableName = "user_roles",
        ChildColumnNames = new[] { childColumn },
        ParentTableSchema = parentSchema,
        ParentTableName = parentTable,
        ParentColumnNames = new[] { "id" },
    };

    /// <summary>archive (read first) and sales each hold users, roles and user_roles.</summary>
    private static IDbModel TwoSchemas(string salesUsersManyToMany) =>
        DbModel.FromTables(
            new List<DbTable>
            {
                Table("archive", "users", t => t.WithPrimaryKey("id")),
                Table("archive", "roles", t => t.WithPrimaryKey("id")),
                Junction("archive"),
                Table("sales", "users", t => t.WithPrimaryKey("id")
                    .WithMetadata(MetadataKeys.Relationships.ManyToMany, salesUsersManyToMany)),
                Table("sales", "roles", t => t.WithPrimaryKey("id")),
                Junction("sales"),
            },
            new PrimaryKeylessTableFixtures.NoOpMetadataLoader(),
            JunctionKeys("archive").Concat(JunctionKeys("sales")).ToList());

    [Theory]
    [InlineData("sales_roles:user_roles")]
    [InlineData("sales_roles:sales.user_roles")]
    public void ManyToManyMetadata_JunctionInSourceSchema_ResolvesThere(string manyToMany)
    {
        var model = TwoSchemas(manyToMany);

        ModelConfigValidator.Validate(model);
        model.GetTableFromDbName("sales", "users").ManyToManyLinks.Values
            .Should().ContainSingle()
            .Which.JunctionTable.Should().BeSameAs(model.GetTableFromDbName("sales", "user_roles"));
    }

    [Fact]
    public void ManyToManyMetadata_QualifiedJunctionInOtherSchema_IsUsedExactly()
    {
        var model = TwoSchemas("sales_roles:archive.user_roles");

        // archive's junction has no foreign keys to sales tables: the qualified name is
        // honoured (not redirected to sales.user_roles), so the entry is reported.
        var problems = ManyToManyDetectionStrategy.ParseMetadata(model, model.GetTableFromDbName("sales", "users")).Problems;
        problems.Should().ContainSingle().Which.Message.Should().Contain("no foreign key to both");
    }

    [Fact]
    public void ManyToManyMetadata_BareJunctionInTwoOtherSchemas_IsReportedAmbiguous()
    {
        var tables = new List<DbTable>
        {
            Table("archive", "users", t => t.WithPrimaryKey("id")),
            Table("archive", "roles", t => t.WithPrimaryKey("id")),
            Junction("archive"),
            Table("sales", "users", t => t.WithPrimaryKey("id")),
            Table("sales", "roles", t => t.WithPrimaryKey("id")),
            Junction("sales"),
            Table("ops", "people", t => t.WithPrimaryKey("id")
                .WithMetadata(MetadataKeys.Relationships.ManyToMany, "sales_roles:user_roles")),
        };
        var model = DbModel.FromTables(tables, new PrimaryKeylessTableFixtures.NoOpMetadataLoader(),
            JunctionKeys("archive").Concat(JunctionKeys("sales")).ToList());

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(MetadataKeys.Relationships.ManyToMany)
            .And.Contain("archive.user_roles").And.Contain("sales.user_roles");
    }

    [Fact]
    public void ManyToManyMetadata_QualifiedJunctionInUnknownSchema_NamesNoJunction()
    {
        var model = TwoSchemas("sales_roles:hr.user_roles");

        var problems = ManyToManyDetectionStrategy.ParseMetadata(model, model.GetTableFromDbName("sales", "users")).Problems;
        problems.Should().ContainSingle().Which.Message.Should().Contain("names no junction table");
    }
}
