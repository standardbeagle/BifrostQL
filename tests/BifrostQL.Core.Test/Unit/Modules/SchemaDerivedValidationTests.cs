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

    #region Server-rendered forms share the same checks

    [Fact]
    public void FormValidator_RefusesUnparseableDatetime_WithExactlyOneError()
    {
        // The shared SchemaDerivedValueValidator owns temporal parseability;
        // ValidateType no longer double-reports the same field.
        var result = new BifrostQL.Core.Forms.BifrostFormValidator().Validate(
            new Dictionary<string, string?> { ["StartedAt"] = "not-a-date" },
            BuildModel().GetTableFromDbName("Orders"), BifrostQL.Core.Forms.FormMode.Insert,
            metadataConfig: null, typeMapper: null);

        result.Errors.Should().ContainSingle()
            .Which.Message.Should().Contain("valid date/time");
    }

    [Fact]
    public void FormValidator_RefusesDatetimeOutsideEngineWindow()
    {
        var result = new BifrostQL.Core.Forms.BifrostFormValidator().Validate(
            new Dictionary<string, string?> { ["StartedAt"] = "1700-01-01" },
            BuildModel().GetTableFromDbName("Orders"), BifrostQL.Core.Forms.FormMode.Insert,
            metadataConfig: null, typeMapper: new RangeAssertingTypeMapper());

        result.Errors.Should().Contain(e => e.Message.Contains("1753"));
    }

    [Fact]
    public void FormValidator_RefusesDecimalIntegerPartOverflow()
    {
        var result = new BifrostQL.Core.Forms.BifrostFormValidator().Validate(
            new Dictionary<string, string?> { ["Price"] = "1234.5" },
            BuildModel().GetTableFromDbName("Orders"), BifrostQL.Core.Forms.FormMode.Insert,
            metadataConfig: null, typeMapper: null);

        result.Errors.Should().Contain(e => e.Message.Contains("3 digits"));
    }

    [Fact]
    public void FormValidator_AcceptsCleanValues()
    {
        var result = new BifrostQL.Core.Forms.BifrostFormValidator().Validate(
            new Dictionary<string, string?>
            {
                ["StartedAt"] = "2024-06-01T12:30:00",
                ["Price"] = "123.45",
                ["Quantity"] = "42",
            },
            BuildModel().GetTableFromDbName("Orders"), BifrostQL.Core.Forms.FormMode.Insert,
            metadataConfig: null, typeMapper: null);

        result.Errors.Should().BeEmpty();
    }

    #endregion

    #region _dbSchema advertises engine temporal windows

    [Fact]
    public void DbSchema_FillsTemporalWindowIntoMinMax_WhenNoMetadataDeclared()
    {
        var column = ResolveDbSchemaColumn(new RangeAssertingTypeMapper(), configureColumn: null);

        column.GetProperty("min").GetString().Should().Be("1753-01-01");
        column.GetProperty("max").GetString().Should().Be("9999-12-31");
    }

    [Fact]
    public void DbSchema_DeclaredMetadataMinWins_OverEngineWindow()
    {
        var column = ResolveDbSchemaColumn(new RangeAssertingTypeMapper(),
            configureColumn: t => t.WithColumnMetadata("StartedAt", MetadataKeys.Validation.Min, "2000-01-01"));

        column.GetProperty("min").GetString().Should().Be("2000-01-01");
        // The undeclared side still gets the engine ceiling.
        column.GetProperty("max").GetString().Should().Be("9999-12-31");
    }

    [Fact]
    public void DbSchema_NoWindow_LeavesMinMaxNull()
    {
        var column = ResolveDbSchemaColumn(typeMapper: null, configureColumn: null);

        column.GetProperty("min").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        column.GetProperty("max").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
    }

    private static System.Text.Json.JsonElement ResolveDbSchemaColumn(
        ITypeMapper? typeMapper,
        Action<DbModelTestFixture.TableBuilder>? configureColumn)
    {
        var fixture = DbModelTestFixture.Create()
            .WithTable("Orders", t =>
            {
                t.WithPrimaryKey("Id").WithColumn("StartedAt", "datetime", isNullable: true);
                configureColumn?.Invoke(t);
            });
        if (typeMapper != null)
            fixture = fixture.WithTypeMapper(typeMapper);
        var model = fixture.Build();

        var resolver = new BifrostQL.Core.Resolvers.MetaSchemaResolver(model);
        var result = resolver.ResolveAsync(new NullArgContext()).AsTask().GetAwaiter().GetResult();
        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        };
        var json = System.Text.Json.JsonSerializer.Serialize(result, options);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .First(t => t.GetProperty("graphQlName").GetString() == "Orders")
            .GetProperty("columns").EnumerateArray()
            .First(c => c.GetProperty("dbName").GetString() == "StartedAt")
            .Clone();
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

    private sealed class NullArgContext : BifrostQL.Core.Resolvers.IBifrostFieldContext
    {
        public string FieldName => "_dbSchema";
        public string? FieldAlias => null;
        public object? Source => null;
        public IReadOnlyList<object> Path => Array.Empty<object>();
        public IDictionary<string, object?> UserContext => new Dictionary<string, object?>();
        public IServiceProvider? RequestServices => null;
        public bool HasSubFields => true;
        public object Document => null!;
        public object Variables => null!;
        public IDictionary<string, object?> InputExtensions => new Dictionary<string, object?>();
        public CancellationToken CancellationToken => CancellationToken.None;
        public bool HasArgument(string name) => false;
        public T? GetArgument<T>(string name) => default;
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
