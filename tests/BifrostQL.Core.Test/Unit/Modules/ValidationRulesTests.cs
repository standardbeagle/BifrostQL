using BifrostQL.Core.Forms;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Validation;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

/// <summary>
/// Tests the shared validation-rule derivation (ValidationRules.ForColumn) and
/// its three consumers staying in sync: server enforcement (date ranges,
/// DB-derived varchar lengths), and the forms builder fallback merge. Together
/// these guarantee a rule declared once in schema metadata validates on both
/// server and client.
/// </summary>
public sealed class ValidationRulesTests
{
    #region Derivation

    [Fact]
    public void ForColumn_ReadsAllValidationMetadata()
    {
        var column = Column("Price", "decimal(10,2)", c => c
            .WithColumnMetadata("Price", MetadataKeys.Validation.Min, "0.01")
            .WithColumnMetadata("Price", MetadataKeys.Validation.Max, "9999")
            .WithColumnMetadata("Price", MetadataKeys.Validation.Step, "0.01")
            .WithColumnMetadata("Price", MetadataKeys.Validation.Required, "true"));

        var rules = ValidationRules.ForColumn(column);

        rules.Min.Should().Be("0.01");
        rules.Max.Should().Be("9999");
        rules.Step.Should().Be("0.01");
        rules.RequiredExplicit.Should().BeTrue();
        rules.TryMinDecimal(out var min).Should().BeTrue();
        min.Should().Be(0.01m);
    }

    [Fact]
    public void ForColumn_DerivesMaxLengthFromVarcharType()
    {
        var rules = ValidationRules.ForColumn(Column("Name", "varchar(50)"));

        rules.MaxLength.Should().Be(50, "the DB schema itself declares the limit");
    }

    [Fact]
    public void ForColumn_MetadataMaxLengthOverridesDbLength()
    {
        var column = Column("Name", "varchar(50)", c => c
            .WithColumnMetadata("Name", MetadataKeys.Validation.MaxLength, "20"));

        ValidationRules.ForColumn(column).MaxLength.Should().Be(20);
    }

    [Theory]
    [InlineData("nvarchar(max)")]
    [InlineData("decimal(10,2)")]
    [InlineData("int")]
    public void ForColumn_NoLengthForNonLengthTypes(string dataType)
    {
        ValidationRules.ForColumn(Column("X", dataType)).MaxLength.Should().BeNull(
            $"{dataType} carries no usable string length");
    }

    [Fact]
    public void ForColumn_DateBounds_ParseAsDates()
    {
        var column = Column("BirthDate", "date", c => c
            .WithColumnMetadata("BirthDate", MetadataKeys.Validation.Min, "1900-01-01")
            .WithColumnMetadata("BirthDate", MetadataKeys.Validation.Max, "2026-12-31"));

        var rules = ValidationRules.ForColumn(column);

        rules.TryMinDate(out var min).Should().BeTrue();
        min.Should().Be(new DateTime(1900, 1, 1));
        rules.TryMaxDate(out var max).Should().BeTrue();
        max.Should().Be(new DateTime(2026, 12, 31));
    }

    [Fact]
    public void ForColumn_RequiredImplied_FromNotNullColumn()
    {
        ValidationRules.ForColumn(Column("Name", "varchar(50)")).RequiredImplied
            .Should().BeTrue("NOT NULL column with no other source must be supplied by the client");
    }

    [Fact]
    public void ForColumn_RequiredImplied_NotForNullableOrAutoPopulated()
    {
        var nullable = Column("Notes", "varchar(50)", nullable: true);
        ValidationRules.ForColumn(nullable).RequiredImplied.Should().BeFalse();

        var populated = Column("CreatedAt", "datetime", c => c
            .WithColumnMetadata("CreatedAt", MetadataKeys.AutoPopulate.Marker, "created-on"));
        ValidationRules.ForColumn(populated).RequiredImplied.Should().BeFalse(
            "auto-populated columns are filled server-side");
    }

    #endregion

    #region Server enforcement

    [Fact]
    public async Task Server_EnforcesDateRange_OnDateTimeValue()
    {
        var (table, model) = DateTable();
        var transformer = new ExtendedServerValidationTransformer();

        var result = await transformer.TransformAsync(table, MutationType.Insert,
            new Dictionary<string, object?> { ["BirthDate"] = new DateTime(1850, 5, 1) },
            Context(model));

        result.Errors.Should().ContainSingle(e => e.Contains("on or after 1900-01-01"));
    }

    [Fact]
    public async Task Server_EnforcesDateRange_OnStringValue()
    {
        var (table, model) = DateTable();
        var transformer = new ExtendedServerValidationTransformer();

        var result = await transformer.TransformAsync(table, MutationType.Insert,
            new Dictionary<string, object?> { ["BirthDate"] = "2300-01-01" },
            Context(model));

        result.Errors.Should().ContainSingle(e => e.Contains("on or before"));
    }

    [Fact]
    public async Task Server_AcceptsDateInsideRange()
    {
        var (table, model) = DateTable();
        var transformer = new ExtendedServerValidationTransformer();

        var result = await transformer.TransformAsync(table, MutationType.Insert,
            new Dictionary<string, object?> { ["BirthDate"] = "1985-06-15" },
            Context(model));

        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Server_EnforcesDbDerivedVarcharLength()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Code", "varchar(5)")
                .WithMetadata(MetadataKeys.Validation.Server, "enabled"))
            .Build();
        var table = model.GetTableFromDbName("Users");
        var transformer = new ExtendedServerValidationTransformer();

        var result = await transformer.TransformAsync(table, MutationType.Insert,
            new Dictionary<string, object?> { ["Code"] = "TOOLONG" },
            Context(model));

        result.Errors.Should().ContainSingle(e => e.Contains("at most 5 characters"),
            "varchar(5) is a declared constraint — reject before the database truncation error");
    }

    #endregion

    #region Forms merge

    [Fact]
    public void FormsMerge_SchemaRulesFillUnconfiguredFields()
    {
        var column = Column("Email", "varchar(100)", c => c
            .WithColumnMetadata("Email", MetadataKeys.Validation.Pattern, "^[^@]+@[^@]+$")
            .WithColumnMetadata("Email", MetadataKeys.Validation.MinLength, "5"));

        var merged = BifrostFormBuilder.MergeWithSchemaRules(column, configured: null);

        merged.Should().NotBeNull();
        merged!.Pattern.Should().Be("^[^@]+@[^@]+$");
        merged.MinLength.Should().Be(5);
        merged.MaxLength.Should().Be(100, "varchar length flows into HTML maxlength");
    }

    [Fact]
    public void FormsMerge_CodeConfigurationWinsPerField()
    {
        var column = Column("Email", "varchar(100)", c => c
            .WithColumnMetadata("Email", MetadataKeys.Validation.Pattern, "schema-pattern")
            .WithColumnMetadata("Email", MetadataKeys.Validation.MaxLength, "80"));
        var configured = new ColumnMetadata { Pattern = "code-pattern" };

        var merged = BifrostFormBuilder.MergeWithSchemaRules(column, configured);

        merged!.Pattern.Should().Be("code-pattern", "code config wins per field");
        merged.MaxLength.Should().Be(80, "unconfigured fields still come from schema metadata");
    }

    [Fact]
    public void FormsMerge_NoRules_ReturnsConfiguredUntouched()
    {
        var column = Column("Notes", "text", nullable: true);
        var configured = new ColumnMetadata { Placeholder = "hi" };

        BifrostFormBuilder.MergeWithSchemaRules(column, configured).Should().BeSameAs(configured);
    }

    #endregion

    #region Helpers

    private static ColumnDto Column(
        string name,
        string dataType,
        Func<DbModelTestFixture.TableBuilder, DbModelTestFixture.TableBuilder>? configure = null,
        bool nullable = false)
    {
        var model = DbModelTestFixture.Create()
            .WithTable("T", t =>
            {
                t.WithPrimaryKey("Id").WithColumn(name, dataType, isNullable: nullable);
                configure?.Invoke(t);
            })
            .Build();
        return model.GetTableFromDbName("T").Columns.First(c => c.ColumnName == name);
    }

    private static (IDbTable table, IDbModel model) DateTable()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("People", t => t
                .WithPrimaryKey("Id")
                .WithColumn("BirthDate", "date", isNullable: true)
                .WithMetadata(MetadataKeys.Validation.Server, "enabled")
                .WithColumnMetadata("BirthDate", MetadataKeys.Validation.Min, "1900-01-01")
                .WithColumnMetadata("BirthDate", MetadataKeys.Validation.Max, "2100-12-31"))
            .Build();
        return (model.GetTableFromDbName("People"), model);
    }

    private static MutationTransformContext Context(IDbModel model) => new()
    {
        Model = model,
        UserContext = new Dictionary<string, object?>(),
    };

    #endregion
}
