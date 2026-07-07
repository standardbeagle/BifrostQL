using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;

namespace BifrostQL.Core.Test.Model;

/// <summary>
/// Tests for fail-fast metadata validation. The validator runs at the end of
/// <c>DbModelLoader.BuildModel</c>; these tests exercise it directly against
/// fixture-built models (the fixture does not route through the loader).
/// </summary>
public class ModelConfigValidatorTests
{
    [Fact]
    public void Validate_BadComputedColumnDependency_ThrowsWithTableAndValue()
    {
        // Arrange: computed-plugin depends on a column that does not exist.
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Email", "nvarchar")
                .WithMetadata(MetadataKeys.Computed.Provider, "score:Int:risk:depends=Id,nonexistent"))
            .Build();

        // Act
        var act = () => ModelConfigValidator.Validate(model);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.Users").And.Contain("nonexistent")
            .And.Contain(MetadataKeys.Computed.Provider);
    }

    [Fact]
    public void Validate_BadComputedSqlPlaceholder_ThrowsWithTableAndValue()
    {
        // Arrange: computed-sql placeholder references a missing column.
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("First", "nvarchar")
                .WithMetadata(MetadataKeys.Computed.Sql, "full:String:{First} || ' ' || {Last}"))
            .Build();

        // Act
        var act = () => ModelConfigValidator.Validate(model);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.Users").And.Contain("Last")
            .And.Contain(MetadataKeys.Computed.Sql);
    }

    [Fact]
    public void Validate_BadTenantFilterColumn_ThrowsWithTableAndValue()
    {
        // Arrange
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal")
                .WithMetadata(MetadataKeys.Security.TenantFilter, "tenant_id"))
            .Build();

        // Act
        var act = () => ModelConfigValidator.Validate(model);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.Orders").And.Contain("tenant_id")
            .And.Contain(MetadataKeys.Security.TenantFilter);
    }

    [Fact]
    public void Validate_BadSoftDeleteColumn_ThrowsWithTableAndValue()
    {
        // Arrange
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal")
                .WithMetadata(MetadataKeys.SoftDelete.Column, "deleted_at"))
            .Build();

        // Act
        var act = () => ModelConfigValidator.Validate(model);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.Orders").And.Contain("deleted_at")
            .And.Contain(MetadataKeys.SoftDelete.Column);
    }

    [Fact]
    public void Validate_SoftDeleteColumn_ByGraphQlNameOnly_Throws()
    {
        // Arrange: the column exists, but soft-delete references its camelCase GraphQL
        // name. Runtime resolves soft-delete through ColumnLookup (the DB name) ONLY, so
        // this would fail at mutation time — the validator must reject it at build.
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("deleted_at", "datetime2", graphQlName: "deletedAt")
                .WithMetadata(MetadataKeys.SoftDelete.Column, "deletedAt"))
            .Build();

        // Act
        var act = () => ModelConfigValidator.Validate(model);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.Orders").And.Contain("deletedAt")
            .And.Contain(MetadataKeys.SoftDelete.Column);
    }

    [Fact]
    public void Validate_BadAutoFilterColumn_ThrowsWithTableAndValue()
    {
        // Arrange: auto-filter maps a non-existent column to a claim.
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal")
                .WithMetadata(MetadataKeys.Security.AutoFilter, "owner_id:user_id"))
            .Build();

        // Act
        var act = () => ModelConfigValidator.Validate(model);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.Orders").And.Contain("owner_id")
            .And.Contain(MetadataKeys.Security.AutoFilter);
    }

    [Fact]
    public void Validate_BadStateMachineColumn_ThrowsWithTableAndValue()
    {
        // Arrange: state-machine state column does not exist.
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal")
                .WithMetadata(MetadataKeys.StateMachine.StateColumn, "status")
                .WithMetadata(MetadataKeys.StateMachine.InitialState, "draft")
                .WithMetadata(MetadataKeys.StateMachine.States, "draft,submitted")
                .WithMetadata(MetadataKeys.StateMachine.Transitions, "draft->submitted"))
            .Build();

        // Act
        var act = () => ModelConfigValidator.Validate(model);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.Orders").And.Contain("status")
            .And.Contain(MetadataKeys.StateMachine.StateColumn);
    }

    [Fact]
    public void Validate_AggregatesMultipleProblems_IntoOneException()
    {
        // Arrange: two distinct problems across two tables.
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithMetadata(MetadataKeys.Security.TenantFilter, "bad_tenant"))
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithMetadata(MetadataKeys.SoftDelete.Column, "bad_deleted"))
            .Build();

        // Act
        var act = () => ModelConfigValidator.Validate(model);

        // Assert: one exception naming both problems.
        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain("bad_tenant");
        message.Should().Contain("bad_deleted");
    }

    [Fact]
    public void Validate_ValidModelWithAllConfigs_DoesNotThrow()
    {
        // Arrange: every validated config present and referencing real columns,
        // mixing DB names and GraphQL names to confirm both resolve.
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("tenant_id", "int")
                .WithColumn("owner_id", "int")
                .WithColumn("deleted_at", "datetime2", isNullable: true)
                .WithColumn("deleted_by", "int", isNullable: true)
                .WithColumn("status", "nvarchar")
                .WithColumn("first_name", "nvarchar", graphQlName: "firstName")
                .WithColumn("last_name", "nvarchar", graphQlName: "lastName")
                .WithMetadata(MetadataKeys.Security.TenantFilter, "tenant_id")
                .WithMetadata(MetadataKeys.SoftDelete.Column, "deleted_at")
                .WithMetadata(MetadataKeys.SoftDelete.DeletedBy, "deleted_by")
                .WithMetadata(MetadataKeys.Security.AutoFilter, "owner_id:user_id")
                .WithMetadata(MetadataKeys.Computed.Sql, "fullName:String:{firstName} || ' ' || {lastName}")
                .WithMetadata(MetadataKeys.Computed.Provider, "score:Int:risk:depends=tenant_id,owner_id")
                .WithMetadata(MetadataKeys.StateMachine.StateColumn, "status")
                .WithMetadata(MetadataKeys.StateMachine.InitialState, "draft")
                .WithMetadata(MetadataKeys.StateMachine.States, "draft,submitted")
                .WithMetadata(MetadataKeys.StateMachine.Transitions, "draft->submitted"))
            .Build();

        // Act
        var act = () => ModelConfigValidator.Validate(model);

        // Assert
        act.Should().NotThrow();
    }
}
