using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;

namespace BifrostQL.Core.Test.Model;

/// <summary>
/// Coverage for three additional fail-fast validation gaps:
///   - a recognized metadata key typed with the wrong casing (e.g.
///     <c>Soft-Delete</c> instead of <c>soft-delete</c>) silently does nothing,
///     because the metadata dictionary is case-sensitive while the
///     "unknown key" warning check is case-insensitive;
///   - a <c>soft-delete</c>/<c>soft-delete-by</c> column that is NOT NULL can
///     never satisfy the <c>IS NULL</c> predicate the soft-delete filter emits,
///     silently hiding every row in the table forever;
///   - <c>eav-*</c> metadata (partial configuration, a nonexistent
///     <c>eav-parent</c>, or nonexistent <c>eav-fk</c>/<c>eav-key</c>/<c>eav-value</c>
///     columns) previously had no startup validation at all.
/// </summary>
public class ModelConfigValidatorSecurityTests
{
    // ---- Case-typo'd metadata key ----

    [Fact]
    public void Validate_KnownTableKeyWithWrongCasing_Throws()
    {
        // "Soft-Delete" (wrong case) is a near-miss of the known "soft-delete"
        // key. The metadata dict is case-sensitive, so the soft-delete
        // transformer's lookup of "soft-delete" would silently miss this and
        // never filter — no error, no warning, feature just doesn't apply.
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("deleted_at", "datetime2", isNullable: true)
                .WithMetadata("Soft-Delete", "deleted_at"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.Orders")
            .And.Contain("Soft-Delete")
            .And.Contain(MetadataKeys.SoftDelete.Column);
    }

    [Fact]
    public void Validate_CorrectlyCasedKnownKey_DoesNotThrowForCasing()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("deleted_at", "datetime2", isNullable: true)
                .WithMetadata(MetadataKeys.SoftDelete.Column, "deleted_at"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_UnknownKeyEntirely_DoesNotThrowForCasing()
    {
        // An entirely unrecognized key (not a near-miss of a known one) is a
        // separate, pre-existing "unknown key" warning path — not a hard
        // failure from this check.
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithMetadata("totally-unknown-key", "value"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().NotThrow();
    }

    // ---- Non-nullable soft-delete column ----

    [Fact]
    public void Validate_NotNullSoftDeleteColumn_Throws()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("is_deleted", "bit", isNullable: false)
                .WithMetadata(MetadataKeys.SoftDelete.Column, "is_deleted"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.Orders")
            .And.Contain("is_deleted")
            .And.Contain("NOT NULL");
    }

    [Fact]
    public void Validate_NotNullSoftDeleteByColumn_Throws()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("deleted_at", "datetime2", isNullable: true)
                .WithColumn("deleted_by", "int", isNullable: false)
                .WithMetadata(MetadataKeys.SoftDelete.Column, "deleted_at")
                .WithMetadata(MetadataKeys.SoftDelete.DeletedBy, "deleted_by"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("deleted_by")
            .And.Contain("NOT NULL");
    }

    [Fact]
    public void Validate_NullableSoftDeleteColumn_DoesNotThrow()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("deleted_at", "datetime2", isNullable: true)
                .WithMetadata(MetadataKeys.SoftDelete.Column, "deleted_at"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().NotThrow();
    }

    // ---- EAV configuration validation ----

    [Fact]
    public void Validate_PartialEavConfig_Throws()
    {
        // Only 3 of the 4 required eav-* keys are present (eav-value missing).
        var model = DbModelTestFixture.Create()
            .WithTable("wp_posts", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("ID"))
            .WithTable("wp_postmeta", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("meta_id")
                .WithColumn("post_id", "int")
                .WithColumn("meta_key", "nvarchar")
                .WithColumn("meta_value", "nvarchar")
                .WithMetadata(MetadataKeys.Eav.Parent, "wp_posts")
                .WithMetadata(MetadataKeys.Eav.ForeignKey, "post_id")
                .WithMetadata(MetadataKeys.Eav.Key, "meta_key"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("wp_postmeta")
            .And.Contain("partial eav-*");
    }

    [Fact]
    public void Validate_EavParentDoesNotExist_Throws()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("wp_postmeta", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("meta_id")
                .WithColumn("post_id", "int")
                .WithColumn("meta_key", "nvarchar")
                .WithColumn("meta_value", "nvarchar")
                .WithMetadata(MetadataKeys.Eav.Parent, "wp_posts_does_not_exist")
                .WithMetadata(MetadataKeys.Eav.ForeignKey, "post_id")
                .WithMetadata(MetadataKeys.Eav.Key, "meta_key")
                .WithMetadata(MetadataKeys.Eav.Value, "meta_value"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("wp_postmeta")
            .And.Contain("eav-parent does not name an existing table");
    }

    [Fact]
    public void Validate_EavForeignKeyColumnDoesNotExist_Throws()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("wp_posts", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("ID"))
            .WithTable("wp_postmeta", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("meta_id")
                .WithColumn("meta_key", "nvarchar")
                .WithColumn("meta_value", "nvarchar")
                .WithMetadata(MetadataKeys.Eav.Parent, "wp_posts")
                .WithMetadata(MetadataKeys.Eav.ForeignKey, "post_id_typo")
                .WithMetadata(MetadataKeys.Eav.Key, "meta_key")
                .WithMetadata(MetadataKeys.Eav.Value, "meta_value"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("wp_postmeta")
            .And.Contain("post_id_typo")
            .And.Contain("eav-fk");
    }

    [Fact]
    public void Validate_ValidEavConfig_DoesNotThrow()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("wp_posts", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("ID"))
            .WithTable("wp_postmeta", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("meta_id")
                .WithColumn("post_id", "int")
                .WithColumn("meta_key", "nvarchar")
                .WithColumn("meta_value", "nvarchar")
                .WithMetadata(MetadataKeys.Eav.Parent, "wp_posts")
                .WithMetadata(MetadataKeys.Eav.ForeignKey, "post_id")
                .WithMetadata(MetadataKeys.Eav.Key, "meta_key")
                .WithMetadata(MetadataKeys.Eav.Value, "meta_value"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().NotThrow();
    }

    // ---- Policy deny-column references ----

    [Fact]
    public void Validate_PolicyReadDenyColumnDoesNotExist_Throws()
    {
        // A typo'd deny column protects NOTHING: the evaluator matches by name, so
        // an absent column is never in the deny set (absent = ALLOW = fail open).
        var model = DbModelTestFixture.Create()
            .WithTable("employees", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("id")
                .WithColumn("salary", "decimal")
                .WithMetadata(MetadataKeys.Policy.ReadDeny, "sallary"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.employees")
            .And.Contain("sallary")
            .And.Contain(MetadataKeys.Policy.ReadDeny);
    }

    [Fact]
    public void Validate_PolicyWriteDenyColumnDoesNotExist_Throws()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("employees", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("id")
                .WithColumn("salary", "decimal")
                .WithMetadata(MetadataKeys.Policy.WriteDeny, "salaryy"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("salaryy")
            .And.Contain(MetadataKeys.Policy.WriteDeny);
    }

    [Fact]
    public void Validate_PolicyRowScopeColumnDoesNotExist_Throws()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("id")
                .WithColumn("tenant_id", "int")
                .WithMetadata(MetadataKeys.Policy.RowScope, "tennant_id = {tenant_id}"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("tennant_id")
            .And.Contain(MetadataKeys.Policy.RowScope);
    }

    [Fact]
    public void Validate_ValidPolicyDenyColumns_DoesNotThrow()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("employees", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("id")
                .WithColumn("salary", "decimal")
                .WithColumn("tenant_id", "int")
                .WithMetadata(MetadataKeys.Policy.ReadDeny, "salary")
                .WithMetadata(MetadataKeys.Policy.WriteDeny, "salary")
                .WithMetadata(MetadataKeys.Policy.RowScope, "tenant_id = {tenant_id}"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().NotThrow();
    }

    // ---- Audit populator values ----

    [Fact]
    public void Validate_UnknownAuditPopulatorValue_Throws()
    {
        // "created_on" (underscore) is a typo of "created-on"; the audit
        // transformer silently never stamps such a column.
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("id")
                .WithColumn("created_at", "datetime2", isNullable: true)
                .WithColumnMetadata("created_at", MetadataKeys.AutoPopulate.Marker, "created_on"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.orders")
            .And.Contain("created_on")
            .And.Contain(MetadataKeys.AutoPopulate.Marker);
    }

    [Fact]
    public void Validate_KnownAuditPopulatorValue_DoesNotThrow()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("id")
                .WithColumn("created_at", "datetime2", isNullable: true)
                .WithColumnMetadata("created_at", MetadataKeys.AutoPopulate.Marker, MetadataKeys.AutoPopulate.CreatedOn))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().NotThrow();
    }

    // ---- Casing of newly allow-listed security keys ----

    [Fact]
    public void Validate_MiscasedPolicyActionsKey_Throws()
    {
        // "Policy-Actions" (wrong case) would silently fail open: the case-sensitive
        // metadata dictionary never surfaces it to the policy engine.
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("id")
                .WithMetadata("Policy-Actions", "read"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("Policy-Actions")
            .And.Contain(MetadataKeys.Policy.Actions);
    }
}
