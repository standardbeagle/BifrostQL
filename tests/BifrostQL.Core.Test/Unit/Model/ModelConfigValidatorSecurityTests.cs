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
}
