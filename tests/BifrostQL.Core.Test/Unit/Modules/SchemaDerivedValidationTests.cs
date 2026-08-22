using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Validation;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

/// <summary>
/// Pins the schema-derived write validation added so that a value the engine
/// would reject on type grounds (unparseable datetime, integer overflow,
/// decimal precision overflow, oversized binary, over-length string) is refused
/// by the server with a clear message BEFORE any SQL runs — on every access
/// method, since ExtendedServerValidationTransformer sits in the unskippable
/// transformer chain. Split by source: rule derivation (ValidationRules) and
/// enforcement (the transformer).
/// </summary>
public sealed class SchemaDerivedValidationTests
{
    #region Rule derivation

    [Fact]
    public void ForColumn_UsesCapturedCharacterMaxLength_ForBareTypeNames()
    {
        // INFORMATION_SCHEMA engines report DATA_TYPE without parens; the
        // captured fact is the only length source there.
        var column = Column("Name", "nvarchar", characterMaxLength: 50);

        ValidationRules.ForColumn(column).MaxLength.Should().Be(50);
    }

    [Fact]
    public void ForColumn_FallsBackToDeclaredTypeLength_WhenNothingCaptured()
    {
        var column = Column("Name", "NVARCHAR(50)");

        ValidationRules.ForColumn(column).MaxLength.Should().Be(50);
    }

    [Fact]
    public void ForColumn_MetadataMaxLengthWins_OverSchemaLength()
    {
        var column = Column("Name", "nvarchar", characterMaxLength: 50, configure: t => t
            .WithColumnMetadata("Name", MetadataKeys.Validation.MaxLength, "10"));

        ValidationRules.ForColumn(column).MaxLength.Should().Be(10);
    }

    [Fact]
    public void ForColumn_RoutesBinaryLengthToBinaryRule_NeverToCharacterMaxLength()
    {
        var column = Column("Payload", "varbinary", characterMaxLength: 16);

        var rules = ValidationRules.ForColumn(column);

        rules.BinaryMaxLength.Should().Be(16);
        rules.MaxLength.Should().BeNull();
    }

    [Fact]
    public void ForColumn_CapturesNumericPrecisionAndScale()
    {
        var captured = Column("Price", "decimal", numericPrecision: 10, numericScale: 2);
        var declared = Column("Price", "DECIMAL(10,2)");

        ValidationRules.ForColumn(captured).Should().BeEquivalentTo(
            ValidationRules.ForColumn(declared),
            o => o.Including(r => r.NumericPrecision).Including(r => r.NumericScale));
        ValidationRules.ForColumn(captured).NumericPrecision.Should().Be(10);
        ValidationRules.ForColumn(captured).NumericScale.Should().Be(2);
    }

    [Theory]
    [InlineData("datetime", TemporalKind.DateTime)]
    [InlineData("datetime2", TemporalKind.DateTime)]
    [InlineData("timestamp without time zone", TemporalKind.DateTime)]
    [InlineData("timestamp with time zone", TemporalKind.DateTimeOffset)]
    [InlineData("datetimeoffset", TemporalKind.DateTimeOffset)]
    [InlineData("date", TemporalKind.DateOnly)]
    [InlineData("time", TemporalKind.TimeOnly)]
    [InlineData("int", TemporalKind.None)]
    [InlineData("nvarchar", TemporalKind.None)]
    public void TemporalKindOf_MapsEngineTypeNames(string dataType, TemporalKind expected)
        => ValidationRules.TemporalKindOf(dataType).Should().Be(expected);

    [Fact]
    public void DeclaredTypeFacts_ParsesLengthsAndPrecision()
    {
        DeclaredTypeFacts.CharacterMaxLength("NVARCHAR(50)").Should().Be(50);
        DeclaredTypeFacts.CharacterMaxLength("varbinary(8)").Should().Be(8);
        DeclaredTypeFacts.CharacterMaxLength("DECIMAL(10,2)").Should().BeNull();
        DeclaredTypeFacts.CharacterMaxLength("text").Should().BeNull();
        DeclaredTypeFacts.PrecisionScale("DECIMAL(10,2)").Should().Be((10, 2));
        DeclaredTypeFacts.PrecisionScale("numeric(5)").Should().Be((5, 0));
        DeclaredTypeFacts.PrecisionScale("nvarchar(50)").Should().Be(((int?)null, (int?)null));
    }

    #endregion

    #region Enforcement

    [Fact]
    public async Task Transform_RefusesUnparseableDatetimeString()
    {
        // Temporal mutation inputs are String on the wire (GetGraphQlInsertTypeName),
        // so nothing upstream has proven they parse; pre-fix this reached the DB.
        var result = await TransformAsync(new Dictionary<string, object?>
        {
            ["StartedAt"] = "not-a-date",
        });

        result.Errors.Should().ContainSingle(e => e.Contains("StartedAt") && e.Contains("valid date/time"));
    }

    [Fact]
    public async Task Transform_AcceptsParseableDatetimeString()
    {
        var result = await TransformAsync(new Dictionary<string, object?>
        {
            ["StartedAt"] = "2024-06-01T12:30:00",
        });

        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Transform_RefusesDatetimeOutsideEngineRange()
    {
        // The dialect's type mapper declares the storable window (SQL Server
        // datetime's 1753 floor is the canonical case).
        var result = await TransformAsync(
            new Dictionary<string, object?> { ["StartedAt"] = "1700-01-01" },
            new RangeAssertingTypeMapper());

        result.Errors.Should().ContainSingle(e => e.Contains("StartedAt") && e.Contains("1753"));
    }

    [Fact]
    public async Task Transform_RefusesIntegerOverflow_ForAdapterSuppliedValues()
    {
        // GraphQL scalar coercion bounds Int on the GraphQL path; protocol
        // adapters reach the pipeline without it, so the pipeline must bound.
        var result = await TransformAsync(new Dictionary<string, object?>
        {
            ["Quantity"] = 3_000_000_000L,
        });

        result.Errors.Should().ContainSingle(e => e.Contains("Quantity") && e.Contains("between"));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData(1.5)]
    public async Task Transform_RefusesNonIntegralValueOnIntegerColumn(object value)
    {
        var result = await TransformAsync(new Dictionary<string, object?> { ["Quantity"] = value });

        result.Errors.Should().ContainSingle(e => e.Contains("Quantity") && e.Contains("whole number"));
    }

    [Fact]
    public async Task Transform_AcceptsIntegerWithinRange()
    {
        var result = await TransformAsync(new Dictionary<string, object?> { ["Quantity"] = 42 });

        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Transform_RefusesDecimalIntegerPartOverflow()
    {
        // decimal(5,2) holds at most 3 integer digits; 1234.5 overflows on
        // every engine. Excess fractional digits round instead (allowed).
        var overflow = await TransformAsync(new Dictionary<string, object?> { ["Price"] = 1234.5m });
        var rounds = await TransformAsync(new Dictionary<string, object?> { ["Price"] = 1.239m });

        overflow.Errors.Should().ContainSingle(e => e.Contains("Price") && e.Contains("3 digits"));
        rounds.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Transform_RefusesOversizedBinary_BytesAndBase64()
    {
        var rawBytes = await TransformAsync(new Dictionary<string, object?>
        {
            ["Thumb"] = new byte[5],
        });
        // 5 zero bytes as base64 — the wire form a client sends.
        var base64 = await TransformAsync(new Dictionary<string, object?>
        {
            ["Thumb"] = Convert.ToBase64String(new byte[5]),
        });
        var fits = await TransformAsync(new Dictionary<string, object?>
        {
            ["Thumb"] = new byte[4],
        });

        rawBytes.Errors.Should().ContainSingle(e => e.Contains("Thumb") && e.Contains("4 bytes"));
        base64.Errors.Should().ContainSingle(e => e.Contains("Thumb") && e.Contains("4 bytes"));
        fits.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Transform_RefusesStringOverCapturedSchemaLength()
    {
        // Bare "nvarchar" + captured length 5: pre-capture this column had no
        // enforceable length at all and the engine threw truncation errors.
        var result = await TransformAsync(new Dictionary<string, object?>
        {
            ["Code"] = "too-long-for-five",
        });

        result.Errors.Should().ContainSingle(e => e.Contains("Code") && e.Contains("at most 5 characters"));
    }

    [Fact]
    public async Task Transform_SchemaChecksRespectColumnOptOut()
    {
        var model = BuildModel(optOutColumn: "StartedAt");
        var table = model.GetTableFromDbName("Orders");
        var transformer = new ExtendedServerValidationTransformer();

        var result = await transformer.TransformAsync(
            table, MutationType.Insert,
            new Dictionary<string, object?> { ["StartedAt"] = "not-a-date" },
            new MutationTransformContext { Model = model, UserContext = new Dictionary<string, object?>() });

        result.Errors.Should().BeEmpty();
    }

    #endregion

    private static async Task<MutationTransformResult> TransformAsync(
        Dictionary<string, object?> data, ITypeMapper? typeMapper = null)
    {
        var model = BuildModel(typeMapper);
        var table = model.GetTableFromDbName("Orders");
        var transformer = new ExtendedServerValidationTransformer();
        return await transformer.TransformAsync(
            table, MutationType.Insert, data,
            new MutationTransformContext { Model = model, UserContext = new Dictionary<string, object?>() });
    }

    private static IDbModel BuildModel(ITypeMapper? typeMapper = null, string? optOutColumn = null)
    {
        var fixture = DbModelTestFixture.Create()
            .WithTable("Orders", t =>
            {
                t.WithPrimaryKey("Id")
                    .WithColumn("StartedAt", "datetime", isNullable: true)
                    .WithColumn("Quantity", "int", isNullable: true)
                    .WithColumn("Price", "decimal", isNullable: true, numericPrecision: 5, numericScale: 2)
                    .WithColumn("Thumb", "varbinary", isNullable: true, characterMaxLength: 4)
                    .WithColumn("Code", "nvarchar", isNullable: true, characterMaxLength: 5);
                if (optOutColumn != null)
                    t.WithColumnMetadata(optOutColumn, MetadataKeys.Validation.Server, "off");
            });
        if (typeMapper != null)
            fixture = fixture.WithTypeMapper(typeMapper);
        return fixture.Build();
    }

    private static ColumnDto Column(
        string name,
        string dataType,
        Func<DbModelTestFixture.TableBuilder, DbModelTestFixture.TableBuilder>? configure = null,
        int? characterMaxLength = null,
        int? numericPrecision = null,
        int? numericScale = null)
    {
        var model = DbModelTestFixture.Create()
            .WithTable("T", t =>
            {
                t.WithPrimaryKey("Id").WithColumn(name, dataType, isNullable: true,
                    characterMaxLength: characterMaxLength,
                    numericPrecision: numericPrecision,
                    numericScale: numericScale);
                configure?.Invoke(t);
            })
            .Build();
        return model.GetTableFromDbName("T").Columns.First(c => c.ColumnName == name);
    }

    /// <summary>
    /// Stand-in for a dialect mapper declaring a storable temporal window
    /// (mirrors SqlServerTypeMapper's datetime bounds; Core.Test does not
    /// reference the dialect packages).
    /// </summary>
    private sealed class RangeAssertingTypeMapper : ITypeMapper
    {
        public string GetGraphQlType(string dataType) => AnsiSqlTypeMapper.Instance.GetGraphQlType(dataType);
        public bool IsSupported(string dataType) => AnsiSqlTypeMapper.Instance.IsSupported(dataType);
        public TemporalValueRange? GetTemporalRange(string dataType)
            => dataType == "datetime"
                ? new TemporalValueRange(new DateTime(1753, 1, 1), new DateTime(9999, 12, 31))
                : null;
    }
}
