using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Validation;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// End to end over the PRODUCTION build path: a real schema read, then
/// <see cref="DbModelLoader.BuildModel"/> (what <c>LoadAsync</c>, the profile cache
/// and the bootstrapper all call), then a write through the transformer chain. The
/// build's column clone once dropped CharacterMaxLength, so the declared length never
/// reached schema-derived validation and SQLite (which does not enforce declared
/// lengths) stored the over-length value.
///
/// The read's type text is reduced to the bare type name ("NVARCHAR") before the
/// build, as the SQL Server, Postgres and MySQL readers report it: SQLite alone keeps
/// "NVARCHAR(5)", and <see cref="ValidationRules"/> re-parses that text when the
/// length is null, which would hide the dropped length on this dialect only.
/// </summary>
public sealed class LoadedModelSchemaLengthValidationTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_loaded_model_length_test;Mode=Memory;Cache=Shared";
    private const string EndpointPath = "/graphql";
    private SqliteConnection _keepAlive = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();
        await Exec("DROP TABLE IF EXISTS gadgets");
        await Exec("CREATE TABLE gadgets (id INTEGER PRIMARY KEY, code NVARCHAR(5) NOT NULL)");
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private static MutationIntentExecutor BuildExecutor()
    {
        var pathCache = new PathCache<Inputs>();
        pathCache.AddLoader(EndpointPath, async () =>
        {
            var factory = new SqliteDbConnFactory(ConnString);
            var metadata = new MetadataLoader(Array.Empty<string>());
            var loader = new DbModelLoader(factory, metadata);
            var read = await loader.ReadAsync();
            var model = loader.BuildModel(WithBareTypeNames(read), metadata);
            return new Inputs(new Dictionary<string, object?>
            {
                ["model"] = model,
                ["connFactory"] = factory,
            });
        });
        var transformers = new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[] { new ExtendedServerValidationTransformer() },
        };
        return new MutationIntentExecutor(pathCache, transformers);
    }

    private static SchemaData WithBareTypeNames(SchemaData read) => read with
    {
        Tables = read.Tables.Cast<DbTable>().Select(WithBareTypeNames).Cast<IDbTable>().ToList(),
    };

    private static DbTable WithBareTypeNames(DbTable table)
    {
        var columns = table.ColumnLookup.ToDictionary(
            kv => kv.Key,
            kv => WithBareTypeName(kv.Value),
            StringComparer.OrdinalIgnoreCase);
        return new DbTable
        {
            DbName = table.DbName,
            GraphQlName = table.GraphQlName,
            NormalizedName = table.NormalizedName,
            TableSchema = table.TableSchema,
            TableType = table.TableType,
            Metadata = new Dictionary<string, object?>(table.Metadata),
            ColumnLookup = columns,
            GraphQlLookup = columns.Values.ToDictionary(c => c.GraphQlName, c => c),
            ColumnPrefixGroups = table.ColumnPrefixGroups,
            Indexes = table.Indexes,
        };
    }

    private static ColumnDto WithBareTypeName(ColumnDto c) => new()
    {
        TableCatalog = c.TableCatalog,
        TableSchema = c.TableSchema,
        TableName = c.TableName,
        ColumnName = c.ColumnName,
        GraphQlName = c.GraphQlName,
        NormalizedName = c.NormalizedName,
        DetectedPrefix = c.DetectedPrefix,
        ColumnRef = c.ColumnRef,
        DataType = c.DataType.Split('(')[0],
        CharacterMaxLength = c.CharacterMaxLength,
        NumericPrecision = c.NumericPrecision,
        NumericScale = c.NumericScale,
        IsNullable = c.IsNullable,
        OrdinalPosition = c.OrdinalPosition,
        IsIdentity = c.IsIdentity,
        IsComputed = c.IsComputed,
        HasDefault = c.HasDefault,
        IsPrimaryKey = c.IsPrimaryKey,
        IsUnique = c.IsUnique,
        Metadata = new Dictionary<string, object?>(c.Metadata),
    };

    [Fact]
    public async Task OverLengthInsert_through_a_loaded_model_is_refused_by_schema_derived_validation()
    {
        // Arrange
        var executor = BuildExecutor();

        // Act
        var act = () => executor.ExecuteAsync(new MutationIntent
        {
            Table = "gadgets",
            Action = MutationIntentAction.Insert,
            Data = new Dictionary<string, object?> { ["code"] = "TOOLONG" },
            UserContext = new Dictionary<string, object?>(),
            Endpoint = EndpointPath,
        });

        // Assert
        await act.Should().ThrowAsync<BifrostExecutionError>()
            .WithMessage("*code must be at most 5 characters*");
        await using var cmd = new SqliteCommand("SELECT COUNT(*) FROM gadgets", _keepAlive);
        Convert.ToInt64(await cmd.ExecuteScalarAsync()).Should().Be(0, "the refused insert wrote nothing");
    }

    [Fact]
    public async Task InLengthInsert_through_a_loaded_model_is_stored()
    {
        // Positive control: the refusal above is about the length, not the table.
        var executor = BuildExecutor();

        var result = await executor.ExecuteAsync(new MutationIntent
        {
            Table = "gadgets",
            Action = MutationIntentAction.Insert,
            Data = new Dictionary<string, object?> { ["code"] = "OK" },
            UserContext = new Dictionary<string, object?>(),
            Endpoint = EndpointPath,
        });

        result.Value.Should().NotBeNull();
    }
}
