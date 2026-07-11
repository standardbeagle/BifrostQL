using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;

namespace BifrostQL.Core.Test.Model;

/// <summary>
/// Covers the central unknown-metadata-key gate in <see cref="ModelConfigValidator"/>: an
/// unrecognized key that is not a consumer extension key (<c>x-</c> prefix) is a hard model-load
/// error rather than a silent no-op, closing the audit-flagged bug class where a typo'd key
/// (e.g. <c>soft-delte</c>) disables a feature with no warning. A deliberate custom key opts
/// out with the <c>x-</c> prefix.
/// </summary>
public class UnknownMetadataKeyGateTests
{
    [Fact]
    public void Validate_UnknownTableKey_Throws()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Email", "nvarchar")
                .WithMetadata("soft-delte", "deleted_at"))  // typo of soft-delete
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.Users").And.Contain("soft-delte")
            .And.Contain("unrecognized table metadata key");
    }

    [Fact]
    public void Validate_UnknownColumnKey_Throws()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Email", "nvarchar")
                .WithColumnMetadata("Email", "max-lenght", "50"))  // typo of max-length
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.Users.Email").And.Contain("max-lenght")
            .And.Contain("unrecognized column metadata key");
    }

    [Fact]
    public void Validate_UnknownDatabaseKey_Throws()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("audit-tabl", "audit_log")  // typo of audit-table
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Email", "nvarchar"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(":root").And.Contain("audit-tabl")
            .And.Contain("unrecognized database metadata key");
    }

    [Fact]
    public void Validate_ConsumerExtensionKeyOnTable_PassesUntouched()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Email", "nvarchar")
                .WithMetadata("x-acme-audit-tag", "pii"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().NotThrow("x- keys are intentional consumer extension keys");
    }

    [Fact]
    public void Validate_ConsumerExtensionKeyOnColumn_PassesUntouched()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Email", "nvarchar")
                .WithColumnMetadata("Email", "x-acme-classification", "restricted"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_ConsumerExtensionPrefixIsCaseInsensitive()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Email", "nvarchar")
                .WithMetadata("X-Acme-Tag", "v"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().NotThrow();
    }

    [Theory]
    // Database-level keys that were previously absent from the allow-list and would have
    // hard-failed real configs once the gate went live (regression for the audit's exact
    // bug class — legit keys never listed): generic-table family, raw-sql family.
    [InlineData("generic-table", "true")]
    [InlineData("generic-table-role", "admin")]
    [InlineData("generic-table-max-rows", "500")]
    [InlineData("generic-table-allowed", "orders,customers")]
    [InlineData("generic-table-denied", "secrets")]
    [InlineData("raw-sql", "enabled")]
    [InlineData("raw-sql-role", "admin")]
    [InlineData("raw-sql-timeout", "30")]
    [InlineData("raw-sql-max-rows", "1000")]
    public void Validate_KnownDatabaseKeys_PassTheGate(string key, string value)
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata(key, value)
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Email", "nvarchar"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().NotThrow($"'{key}' is a recognized database metadata key");
    }

    [Fact]
    public void Validate_RecognizedKeys_DoNotTripTheGate()
    {
        // A model using only built-in keys (with valid column references) passes the gate
        // — the gate must not produce false positives on the allow-list.
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("TenantId", "int")
                .WithColumn("Email", "nvarchar")
                .WithMetadata(MetadataKeys.Security.TenantFilter, "TenantId")
                .WithColumnMetadata("Email", MetadataKeys.Ui.Label, "Email address"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().NotThrow();
    }
}
