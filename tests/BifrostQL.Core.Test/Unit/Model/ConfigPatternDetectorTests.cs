using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;

namespace BifrostQL.Core.Test.Model;

public class ConfigPatternDetectorTests
{
    #region Detection - Soft Delete

    [Fact]
    public void Detect_SoftDelete_DeletedAt()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("deleted_at", "datetime2", isNullable: true))
            .Build();

        var detector = new ConfigPatternDetector();
        var results = detector.Detect(model);

        results.Should().HaveCount(1);
        results[0].SoftDelete.Should().NotBeNull();
        results[0].SoftDelete!.ColumnName.Should().Be("deleted_at");
    }

    [Fact]
    public void Detect_SoftDelete_IsDeleted()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("is_deleted", "bit"))
            .Build();

        var detector = new ConfigPatternDetector();
        var results = detector.Detect(model);

        results.Should().HaveCount(1);
        results[0].SoftDelete!.ColumnName.Should().Be("is_deleted");
    }

    [Fact]
    public void Detect_SoftDelete_CaseInsensitive()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Items", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Deleted_At", "datetime2", isNullable: true))
            .Build();

        var detector = new ConfigPatternDetector();
        var results = detector.Detect(model);

        results.Should().HaveCount(1);
        results[0].SoftDelete!.ColumnName.Should().Be("Deleted_At");
    }

    [Fact]
    public void Detect_NoSoftDelete_WhenNoMatchingColumn()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .Build();

        var detector = new ConfigPatternDetector();
        var results = detector.Detect(model);

        results.Should().BeEmpty();
    }

    #endregion

    #region Detection - Tenant

    [Fact]
    public void Detect_Tenant_TenantId()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("tenant_id", "int"))
            .Build();

        var detector = new ConfigPatternDetector();
        var results = detector.Detect(model);

        results.Should().HaveCount(1);
        results[0].Tenant.Should().NotBeNull();
        results[0].Tenant!.ColumnName.Should().Be("tenant_id");
    }

    [Fact]
    public void Detect_Tenant_OrganizationId()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("organization_id", "int"))
            .Build();

        var detector = new ConfigPatternDetector();
        var results = detector.Detect(model);

        results.Should().HaveCount(1);
        results[0].Tenant!.ColumnName.Should().Be("organization_id");
    }

    [Fact]
    public void Detect_Tenant_CompanyId()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("company_id", "int"))
            .Build();

        var detector = new ConfigPatternDetector();
        var results = detector.Detect(model);

        results.Should().HaveCount(1);
        results[0].Tenant!.ColumnName.Should().Be("company_id");
    }

    #endregion

    #region Detection - Audit Columns

    [Fact]
    public void Detect_AuditColumns_CreatedAt()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("created_at", "datetime2"))
            .Build();

        var detector = new ConfigPatternDetector();
        var results = detector.Detect(model);

        results.Should().HaveCount(1);
        results[0].AuditColumns.Should().HaveCount(1);
        results[0].AuditColumns[0].ColumnName.Should().Be("created_at");
        results[0].AuditColumns[0].Role.Should().Be(AuditRole.CreatedOn);
    }

    [Fact]
    public void Detect_AuditColumns_MultipleRoles()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("created_at", "datetime2")
                .WithColumn("created_by", "int")
                .WithColumn("updated_at", "datetime2")
                .WithColumn("updated_by", "int"))
            .Build();

        var detector = new ConfigPatternDetector();
        var results = detector.Detect(model);

        results.Should().HaveCount(1);
        var audit = results[0].AuditColumns;
        audit.Should().HaveCount(4);
        audit.Should().Contain(a => a.Role == AuditRole.CreatedOn);
        audit.Should().Contain(a => a.Role == AuditRole.CreatedBy);
        audit.Should().Contain(a => a.Role == AuditRole.UpdatedOn);
        audit.Should().Contain(a => a.Role == AuditRole.UpdatedBy);
    }

    [Fact]
    public void Detect_AuditColumns_CreatedByWildcard()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("created_by_user_id", "int"))
            .Build();

        var detector = new ConfigPatternDetector();
        var results = detector.Detect(model);

        results.Should().HaveCount(1);
        results[0].AuditColumns[0].ColumnName.Should().Be("created_by_user_id");
        results[0].AuditColumns[0].Role.Should().Be(AuditRole.CreatedBy);
    }

    [Fact]
    public void Detect_AuditColumns_ModifiedVariants()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("modified_at", "datetime2")
                .WithColumn("modified_by", "int"))
            .Build();

        var detector = new ConfigPatternDetector();
        var results = detector.Detect(model);

        results.Should().HaveCount(1);
        results[0].AuditColumns.Should().Contain(a => a.Role == AuditRole.UpdatedOn && a.ColumnName == "modified_at");
        results[0].AuditColumns.Should().Contain(a => a.Role == AuditRole.UpdatedBy && a.ColumnName == "modified_by");
    }

    #endregion

    #region Detection - Combined

    [Fact]
    public void Detect_CombinedPatterns_AllDetected()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("tenant_id", "int")
                .WithColumn("deleted_at", "datetime2", isNullable: true)
                .WithColumn("created_at", "datetime2")
                .WithColumn("updated_at", "datetime2"))
            .Build();

        var detector = new ConfigPatternDetector();
        var results = detector.Detect(model);

        results.Should().HaveCount(1);
        var r = results[0];
        r.SoftDelete.Should().NotBeNull();
        r.Tenant.Should().NotBeNull();
        // 3 audit columns: created_at (CreatedOn), deleted_at (DeletedOn), updated_at (UpdatedOn)
        r.AuditColumns.Should().HaveCount(3);
    }

    [Fact]
    public void Detect_MultipleTables_ReturnsOnlyMatches()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("tenant_id", "int"))
            .WithTable("Logs", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Message", "nvarchar"))
            .Build();

        var detector = new ConfigPatternDetector();
        var results = detector.Detect(model);

        results.Should().HaveCount(1);
        results[0].TableName.Should().Be("Users");
    }

    [Fact]
    public void Detect_DeletedAt_IsDetectedAsBothSoftDeleteAndAudit()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Items", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("deleted_at", "datetime2", isNullable: true))
            .Build();

        var detector = new ConfigPatternDetector();
        var results = detector.Detect(model);

        results.Should().HaveCount(1);
        results[0].SoftDelete!.ColumnName.Should().Be("deleted_at");
        results[0].AuditColumns.Should().Contain(a => a.Role == AuditRole.DeletedOn && a.ColumnName == "deleted_at");
    }

    #endregion

    #region Detection - Custom Patterns

    [Fact]
    public void Detect_CustomPatterns_OverrideDefaults()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("custom_tenant", "int"))
            .Build();

        var patterns = new DetectionPatterns
        {
            TenantColumns = new[] { DetectionPatterns.Pattern("custom_tenant") },
        };

        var detector = new ConfigPatternDetector(patterns);
        var results = detector.Detect(model);

        results.Should().HaveCount(1);
        results[0].Tenant!.ColumnName.Should().Be("custom_tenant");
    }

    #endregion

    #region TablePatternResult

    [Fact]
    public void TablePatternResult_QualifiedName_WithSchema()
    {
        var result = new TablePatternResult("dbo", "Users", null, null, Array.Empty<AuditColumnPattern>());
        result.QualifiedName.Should().Be("dbo.Users");
    }

    [Fact]
    public void TablePatternResult_QualifiedName_NoSchema()
    {
        var result = new TablePatternResult("", "Users", null, null, Array.Empty<AuditColumnPattern>());
        result.QualifiedName.Should().Be("Users");
    }

    [Fact]
    public void TablePatternResult_HasPatterns_FalseWhenEmpty()
    {
        var result = new TablePatternResult("dbo", "Users", null, null, Array.Empty<AuditColumnPattern>());
        result.HasPatterns.Should().BeFalse();
    }

    [Fact]
    public void TablePatternResult_HasPatterns_TrueWithSoftDelete()
    {
        var result = new TablePatternResult("dbo", "Users", new SoftDeletePattern("deleted_at"), null, Array.Empty<AuditColumnPattern>());
        result.HasPatterns.Should().BeTrue();
    }

    #endregion
}

public class ConfigGeneratorTests
{
    [Fact]
    public void Generate_SoftDeleteRule()
    {
        var result = new TablePatternResult("dbo", "Users",
            new SoftDeletePattern("deleted_at"), null, Array.Empty<AuditColumnPattern>());

        var generator = new ConfigGenerator();
        var rules = generator.GenerateTableRules(result);

        rules.Should().HaveCount(1);
        rules[0].Should().Be("dbo.Users { soft-delete: deleted_at }");
    }

    [Fact]
    public void Generate_TenantFilterRule()
    {
        var result = new TablePatternResult("dbo", "Orders",
            null, new TenantPattern("tenant_id"), Array.Empty<AuditColumnPattern>());

        var generator = new ConfigGenerator();
        var rules = generator.GenerateTableRules(result);

        rules.Should().HaveCount(1);
        rules[0].Should().Be("dbo.Orders { tenant-filter: tenant_id }");
    }

    [Fact]
    public void Generate_CombinedSoftDeleteAndTenant()
    {
        var result = new TablePatternResult("dbo", "Orders",
            new SoftDeletePattern("deleted_at"),
            new TenantPattern("tenant_id"),
            Array.Empty<AuditColumnPattern>());

        var generator = new ConfigGenerator();
        var rules = generator.GenerateTableRules(result);

        rules.Should().HaveCount(1);
        rules[0].Should().Be("dbo.Orders { soft-delete: deleted_at; tenant-filter: tenant_id }");
    }

    [Fact]
    public void Generate_AuditColumnRules()
    {
        var auditColumns = new[]
        {
            new AuditColumnPattern("created_at", AuditRole.CreatedOn),
            new AuditColumnPattern("created_by", AuditRole.CreatedBy),
        };
        var result = new TablePatternResult("dbo", "Users", null, null, auditColumns);

        var generator = new ConfigGenerator();
        var rules = generator.GenerateTableRules(result);

        rules.Should().HaveCount(2);
        rules[0].Should().Be("dbo.Users.created_at { populate: created-on }");
        rules[1].Should().Be("dbo.Users.created_by { populate: created-by }");
    }

    [Fact]
    public void Generate_AllAuditRoles()
    {
        var auditColumns = new[]
        {
            new AuditColumnPattern("created_at", AuditRole.CreatedOn),
            new AuditColumnPattern("created_by", AuditRole.CreatedBy),
            new AuditColumnPattern("updated_at", AuditRole.UpdatedOn),
            new AuditColumnPattern("updated_by", AuditRole.UpdatedBy),
            new AuditColumnPattern("deleted_at", AuditRole.DeletedOn),
            new AuditColumnPattern("deleted_by", AuditRole.DeletedBy),
        };
        var result = new TablePatternResult("dbo", "Users", null, null, auditColumns);

        var generator = new ConfigGenerator();
        var rules = generator.GenerateTableRules(result);

        rules.Should().HaveCount(6);
        rules[0].Should().Contain("populate: created-on");
        rules[1].Should().Contain("populate: created-by");
        rules[2].Should().Contain("populate: updated-on");
        rules[3].Should().Contain("populate: updated-by");
        rules[4].Should().Contain("populate: deleted-on");
        rules[5].Should().Contain("populate: deleted-by");
    }

    [Fact]
    public void Generate_FullPipeline_DetectThenGenerate()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("tenant_id", "int")
                .WithColumn("deleted_at", "datetime2", isNullable: true)
                .WithColumn("created_at", "datetime2")
                .WithColumn("updated_by", "int"))
            .Build();

        var detector = new ConfigPatternDetector();
        var results = detector.Detect(model);

        var generator = new ConfigGenerator();
        var rules = generator.Generate(results);

        // 1 table-level rule (soft-delete + tenant) + 3 audit column rules
        rules.Should().HaveCount(4);
        rules[0].Should().Contain("soft-delete: deleted_at");
        rules[0].Should().Contain("tenant-filter: tenant_id");
    }

    [Fact]
    public void Generate_MultipleTables()
    {
        var results = new[]
        {
            new TablePatternResult("dbo", "Users",
                null, new TenantPattern("tenant_id"), Array.Empty<AuditColumnPattern>()),
            new TablePatternResult("dbo", "Orders",
                new SoftDeletePattern("deleted_at"), null, Array.Empty<AuditColumnPattern>()),
        };

        var generator = new ConfigGenerator();
        var rules = generator.Generate(results);

        rules.Should().HaveCount(2);
        rules.Should().Contain("dbo.Users { tenant-filter: tenant_id }");
        rules.Should().Contain("dbo.Orders { soft-delete: deleted_at }");
    }

    [Fact]
    public void Generate_EmptyResults_ReturnsEmptyList()
    {
        var generator = new ConfigGenerator();
        var rules = generator.Generate(Array.Empty<TablePatternResult>());
        rules.Should().BeEmpty();
    }
}

public class ConfigValidatorTests
{
    #region Valid Rules

    [Fact]
    public void Validate_ValidTableRule_NoIssues()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("tenant_id", "int"))
            .Build();

        var validator = new ConfigValidator();
        var issues = validator.Validate(new[] { "dbo.Users { tenant-filter: tenant_id }" }, model);

        issues.Should().BeEmpty();
    }

    [Fact]
    public void Validate_ValidRootRule_NoIssues()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id"))
            .Build();

        var validator = new ConfigValidator();
        var issues = validator.Validate(new[] { ":root { auto-join: true }" }, model);

        issues.Should().BeEmpty();
    }

    [Fact]
    public void Validate_ValidColumnRule_NoIssues()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("created_at", "datetime2"))
            .Build();

        var validator = new ConfigValidator();
        var issues = validator.Validate(new[] { "dbo.Users.created_at { populate: created-on }" }, model);

        issues.Should().BeEmpty();
    }

    #endregion

    #region Invalid Format

    [Fact]
    public void Validate_InvalidFormat_ReturnsError()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id"))
            .Build();

        var validator = new ConfigValidator();
        var issues = validator.Validate(new[] { "no braces here" }, model);

        issues.Should().HaveCount(1);
        issues[0].Severity.Should().Be(ConfigIssueSeverity.Error);
    }

    [Fact]
    public void Validate_EmptyProperties_ReturnsError()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id"))
            .Build();

        var validator = new ConfigValidator();
        var issues = validator.Validate(new[] { "dbo.Users { }" }, model);

        issues.Should().HaveCount(1);
        issues[0].Severity.Should().Be(ConfigIssueSeverity.Error);
    }

    #endregion

    #region Missing Table/Column

    [Fact]
    public void Validate_MissingTable_ReturnsError()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id"))
            .Build();

        var validator = new ConfigValidator();
        var issues = validator.Validate(new[] { "dbo.NonExistent { soft-delete: col }" }, model);

        issues.Should().HaveCount(1);
        issues[0].Severity.Should().Be(ConfigIssueSeverity.Error);
        issues[0].Message.Should().Contain("not found");
    }

    [Fact]
    public void Validate_MissingColumn_ReturnsError()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id"))
            .Build();

        var validator = new ConfigValidator();
        var issues = validator.Validate(new[] { "dbo.Users.nonexistent { populate: created-on }" }, model);

        issues.Should().HaveCount(1);
        issues[0].Severity.Should().Be(ConfigIssueSeverity.Error);
        issues[0].Message.Should().Contain("not found");
    }

    [Fact]
    public void Validate_MissingSoftDeleteColumn_ReturnsError()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .Build();

        var validator = new ConfigValidator();
        var issues = validator.Validate(new[] { "dbo.Users { soft-delete: missing_col }" }, model);

        issues.Should().Contain(i => i.Severity == ConfigIssueSeverity.Error && i.Message.Contains("missing_col"));
    }

    [Fact]
    public void Validate_MissingTenantFilterColumn_ReturnsError()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .Build();

        var validator = new ConfigValidator();
        var issues = validator.Validate(new[] { "dbo.Users { tenant-filter: missing_col }" }, model);

        issues.Should().Contain(i => i.Severity == ConfigIssueSeverity.Error && i.Message.Contains("missing_col"));
    }

    #endregion

    #region Wildcard Selectors

    [Fact]
    public void Validate_WildcardTable_SkipsTableCheck()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id"))
            .Build();

        var validator = new ConfigValidator();
        var issues = validator.Validate(new[] { "dbo.* { soft-delete: deleted_at }" }, model);

        issues.Should().BeEmpty();
    }

    [Fact]
    public void Validate_WildcardColumn_SkipsColumnCheck()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id"))
            .Build();

        var validator = new ConfigValidator();
        var issues = validator.Validate(new[] { "dbo.Users.* { populate: created-on }" }, model);

        issues.Should().BeEmpty();
    }

    #endregion

    #region Unknown Keys

    [Fact]
    public void Validate_UnknownTableKey_ReturnsWarning()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id"))
            .Build();

        var validator = new ConfigValidator();
        var issues = validator.Validate(new[] { "dbo.Users { fake-key: value }" }, model);

        issues.Should().Contain(i => i.Severity == ConfigIssueSeverity.Warning);
    }

    [Fact]
    public void Validate_UnknownDatabaseKey_ReturnsWarning()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id"))
            .Build();

        var validator = new ConfigValidator();
        var issues = validator.Validate(new[] { ":root { not-real: value }" }, model);

        issues.Should().Contain(i => i.Severity == ConfigIssueSeverity.Warning);
    }

    #endregion

    #region End-to-End: Detect -> Generate -> Validate

    [Fact]
    public void EndToEnd_DetectedRules_ValidateCleanly()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("tenant_id", "int")
                .WithColumn("deleted_at", "datetime2", isNullable: true)
                .WithColumn("created_at", "datetime2")
                .WithColumn("created_by", "int")
                .WithColumn("updated_at", "datetime2")
                .WithColumn("updated_by", "int"))
            .Build();

        var detector = new ConfigPatternDetector();
        var detected = detector.Detect(model);

        var generator = new ConfigGenerator();
        var rules = generator.Generate(detected);

        var validator = new ConfigValidator();
        var issues = validator.Validate(rules, model);

        issues.Where(i => i.Severity == ConfigIssueSeverity.Error).Should().BeEmpty();
    }

    [Fact]
    public void EndToEnd_MultipleTables_AllRulesValid()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("tenant_id", "int")
                .WithColumn("created_at", "datetime2"))
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("deleted_at", "datetime2", isNullable: true)
                .WithColumn("updated_at", "datetime2"))
            .Build();

        var detector = new ConfigPatternDetector();
        var detected = detector.Detect(model);

        var generator = new ConfigGenerator();
        var rules = generator.Generate(detected);

        var validator = new ConfigValidator();
        var issues = validator.Validate(rules, model);

        issues.Where(i => i.Severity == ConfigIssueSeverity.Error).Should().BeEmpty();
    }

    #endregion

    #region Multiple Rules

    [Fact]
    public void Validate_MultipleRules_CollectsAllIssues()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id"))
            .Build();

        var validator = new ConfigValidator();
        var issues = validator.Validate(new[]
        {
            "dbo.Missing { soft-delete: col }",
            "no braces",
            "dbo.Users { tenant-filter: missing_col }"
        }, model);

        issues.Where(i => i.Severity == ConfigIssueSeverity.Error).Should().HaveCountGreaterThanOrEqualTo(3);
    }

    #endregion
}
