using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Model;

/// <summary>
/// The polymorphic-* keys and the metadata form of many-to-many used to be read only by their
/// relationship strategies, which skipped any entry they could not resolve. A typo
/// (<c>polymorphic-map: post=postz</c>) loaded green and the relationship never appeared.
/// Model load now reports every entry the strategy would skip.
/// </summary>
public sealed class RelationshipMetadataValidationTests
{
    private static DbModelTestFixture Notes(string map, string typeColumn = "entity_type", string idColumn = "entity_id") =>
        DbModelTestFixture.Create()
            .WithTable("post", t => t.WithSchema("dbo").WithPrimaryKey("id"))
            .WithTable("photo", t => t.WithSchema("dbo").WithPrimaryKey("id"))
            .WithTable("notes", t => t.WithSchema("dbo").WithPrimaryKey("id")
                .WithColumn("entity_type", "nvarchar")
                .WithColumn("entity_id", "int")
                .WithMetadata(MetadataKeys.Relationships.PolymorphicTypeCol, typeColumn)
                .WithMetadata(MetadataKeys.Relationships.PolymorphicIdCol, idColumn)
                .WithMetadata(MetadataKeys.Relationships.PolymorphicMap, map));

    [Fact]
    public void Validate_PolymorphicMapResolves_DoesNotThrow()
    {
        var act = () => ModelConfigValidator.Validate(Notes("post=post,photo=photo").Build());

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("post=postz", "postz")]
    [InlineData("post=post,photo", "photo")]
    [InlineData("post=post,=photo", "=photo")]
    public void Validate_PolymorphicMapEntryThatCannotResolve_Throws(string map, string offender)
    {
        var act = () => ModelConfigValidator.Validate(Notes(map).Build());

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(MetadataKeys.Relationships.PolymorphicMap).And.Contain(offender);
    }

    [Theory]
    [InlineData("kind", "entity_id", "polymorphic-type-column")]
    [InlineData("entity_type", "target_id", "polymorphic-id-column")]
    public void Validate_PolymorphicColumnMissing_Throws(string typeColumn, string idColumn, string key)
    {
        var act = () => ModelConfigValidator.Validate(Notes("post=post", typeColumn, idColumn).Build());

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(key);
    }

    [Fact]
    public void Validate_PolymorphicParentWithCompositeKey_Throws()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("post", t => t.WithSchema("dbo").WithPrimaryKey("id").WithPrimaryKey("rev"))
            .WithTable("notes", t => t.WithSchema("dbo").WithPrimaryKey("id")
                .WithColumn("entity_type", "nvarchar")
                .WithColumn("entity_id", "int")
                .WithMetadata(MetadataKeys.Relationships.PolymorphicTypeCol, "entity_type")
                .WithMetadata(MetadataKeys.Relationships.PolymorphicIdCol, "entity_id")
                .WithMetadata(MetadataKeys.Relationships.PolymorphicMap, "post=post"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(MetadataKeys.Relationships.PolymorphicMap).And.Contain("single-column primary key");
    }

    private static IDbModel UsersRoles(string manyToMany) =>
        DbModelTestFixture.Create()
            .WithTable("Users", t => t.WithSchema("dbo").WithPrimaryKey("Id")
                .WithMetadata(MetadataKeys.Relationships.ManyToMany, manyToMany))
            .WithTable("Roles", t => t.WithSchema("dbo").WithPrimaryKey("Id"))
            .WithTable("Assignments", t => t.WithSchema("dbo").WithPrimaryKey("Id")
                .WithColumn("UserId", "int").WithColumn("RoleId", "int"))
            .WithTable("Unrelated", t => t.WithSchema("dbo").WithPrimaryKey("Id"))
            .WithForeignKey("FK_Assignments_Users", "Assignments", "UserId", "Users", "Id")
            .WithForeignKey("FK_Assignments_Roles", "Assignments", "RoleId", "Roles", "Id")
            .Build();

    [Theory]
    [InlineData("Roles:Assignments")]
    [InlineData("!Roles:Assignments")]
    public void Validate_ManyToManyMetadataResolves_DoesNotThrow(string manyToMany)
    {
        var act = () => ModelConfigValidator.Validate(UsersRoles(manyToMany));

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("Rolez:Assignments", "Rolez")]
    [InlineData("Roles:Asignments", "Asignments")]
    [InlineData("Roles", "Roles")]
    [InlineData("Roles:Unrelated", "Unrelated")]
    public void Validate_ManyToManyMetadataThatCannotResolve_Throws(string manyToMany, string offender)
    {
        var act = () => ModelConfigValidator.Validate(UsersRoles(manyToMany));

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(MetadataKeys.Relationships.ManyToMany).And.Contain(offender);
    }
}
