using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using BifrostQL.MySql;
using BifrostQL.Ngsql;
using BifrostQL.Sqlite;
using BifrostQL.SqlServer;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Model;

/// <summary>
/// Bare <c>timestamp</c> means two different things: SQL Server's rowversion (an 8-byte
/// binary counter the engine advances on every write) and a date-time everywhere else.
/// One fact per dialect pins which meaning its mapper gives the name, then the SQL Server
/// meaning is followed into the schema (read-only, update input only as the token) and the
/// concurrency guard (compared as binary, never bumped by Bifrost).
/// </summary>
[Collection(BifrostQL.Core.Test.ErrorSinkLoggerCollection.Name)]
public sealed class BareTimestampDialectMappingTests
{
    [Fact]
    public void SqlServer_BareTimestamp_IsBinaryRowVersion_NotDateTime()
    {
        ITypeMapper mapper = SqlServerTypeMapper.Instance;

        mapper.IsRowVersion("timestamp").Should().BeTrue();
        mapper.IsRowVersion("TIMESTAMP").Should().BeTrue();
        mapper.IsRowVersion("rowversion").Should().BeTrue();
        mapper.GetGraphQlType("timestamp").Should().Be("String", "a row version travels as base64 like any binary column");
        mapper.GetGraphQlInsertTypeName("timestamp").Should().Be("String!");
        mapper.GetTemporalRange("timestamp").Should().BeNull();
        mapper.IsRowVersion("datetime2").Should().BeFalse();
    }

    [Fact]
    public void MySql_BareTimestamp_IsDateTime()
    {
        ITypeMapper mapper = MySqlTypeMapper.Instance;

        mapper.IsRowVersion("timestamp").Should().BeFalse();
        mapper.GetGraphQlType("timestamp").Should().Be("DateTime");
        mapper.GetTemporalRange("timestamp").Should().NotBeNull("MySQL TIMESTAMP is the 1970..2038 date-time");
    }

    [Fact]
    public void Postgres_BareTimestamp_IsTemporal()
    {
        ITypeMapper mapper = PostgresTypeMapper.Instance;

        mapper.IsRowVersion("timestamp").Should().BeFalse();
        PostgresDialect.IsTemporalType("timestamp").Should().BeTrue("timestamp is 'timestamp without time zone'");
        mapper.GetGraphQlType("timestamp").Should().Be("String", "Postgres temporals travel as ISO strings");
    }

    [Fact]
    public void Sqlite_BareTimestamp_IsDateTime()
    {
        ITypeMapper mapper = SqliteTypeMapper.Instance;

        mapper.IsRowVersion("timestamp").Should().BeFalse();
        mapper.GetGraphQlType("timestamp").Should().Be("DateTime");
    }

    private static IDbModel RowVersionModel(ITypeMapper mapper, bool tokenIsComputed = true) =>
        DbModelTestFixture.Create()
            .WithTypeMapper(mapper)
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("row_version", "timestamp", hasDefault: true, isComputed: tokenIsComputed)
                .WithMetadata(MetadataKeys.Concurrency.Token, "row_version"))
            .Build();

    private static MutationTransformContext Context(IDbModel model) => new()
    {
        Model = model,
        UserContext = new Dictionary<string, object?>(),
    };

    [Fact]
    public async Task SqlServer_RowVersionToken_GuardsOnBinary_AndLeavesTheSet()
    {
        var model = RowVersionModel(SqlServerTypeMapper.Instance);
        var table = model.GetTableFromDbName("Orders");
        var read = new byte[] { 0, 0, 0, 0, 0, 0, 0x07, 0xD1 };
        var data = new Dictionary<string, object?>
        {
            ["Id"] = 1, ["Name"] = "changed", ["row_version"] = Convert.ToBase64String(read),
        };

        var result = await new ConcurrencyMutationTransformer()
            .TransformAsync(table, MutationType.Update, data, Context(model));

        result.Errors.Should().BeEmpty();
        result.ConflictOnNoRows.Should().BeTrue();
        result.AdditionalFilter!.ColumnName.Should().Be("row_version");
        result.AdditionalFilter.Next!.Value.Should().BeOfType<byte[]>().Which.Should().Equal(read);
        result.Data.Should().NotContainKey("row_version", "the engine advances a rowversion; writing it fails");
        result.Data["Name"].Should().Be("changed");
    }

    [Fact]
    public async Task SqlServer_RowVersionToken_NotBase64_IsRejected()
    {
        var model = RowVersionModel(SqlServerTypeMapper.Instance);
        var table = model.GetTableFromDbName("Orders");
        var data = new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = "x", ["row_version"] = "not base64!" };

        var result = await new ConcurrencyMutationTransformer()
            .TransformAsync(table, MutationType.Update, data, Context(model));

        result.Errors.Should().ContainSingle().Which.Should().Contain("not a base64 row version");
    }

    [Fact]
    public async Task MySql_TimestampToken_IsADateTimeToken_AndRestamps()
    {
        var model = RowVersionModel(MySqlTypeMapper.Instance, tokenIsComputed: false);
        var table = model.GetTableFromDbName("Orders");
        var data = new Dictionary<string, object?>
        {
            ["Id"] = 1, ["Name"] = "x", ["row_version"] = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        var result = await new ConcurrencyMutationTransformer()
            .TransformAsync(table, MutationType.Update, data, Context(model));

        result.Errors.Should().BeEmpty();
        result.Data["row_version"].Should().BeOfType<DateTimeOffset>("a MySQL timestamp token is restamped");
    }

    [Fact]
    public void SqlServer_RowVersion_IsAbsentFromInsertInput_AndPresentInUpdateInputAsToken()
    {
        var model = RowVersionModel(SqlServerTypeMapper.Instance);
        var generator = new TableSchemaGenerator(model.GetTableFromDbName("Orders"));

        var insert = generator.GetMutationParameterType(MutateActions.Insert, IdentityType.None);
        var update = generator.GetMutationParameterType(MutateActions.Update, IdentityType.Required);

        insert.Split('\n').Should().NotContain(l => l.TrimStart().StartsWith("row_version :"),
            "a rowversion is engine-assigned and cannot be inserted");
        insert.Split('\n').Should().Contain(l => l.TrimStart().StartsWith("Name :"));
        update.Split('\n').Should().ContainSingle(l => l.TrimStart().StartsWith("row_version :"),
            "the client sends the version it read so the update can be guarded");
    }

    [Fact]
    public void ComputedColumnThatIsNotTheToken_StaysOutOfTheUpdateInput()
    {
        var model = DbModelTestFixture.Create()
            .WithTypeMapper(SqlServerTypeMapper.Instance)
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("row_version", "timestamp", hasDefault: true, isComputed: true))
            .Build();

        var update = new TableSchemaGenerator(model.GetTableFromDbName("Orders"))
            .GetMutationParameterType(MutateActions.Update, IdentityType.Required);

        update.Split('\n').Should().NotContain(l => l.TrimStart().StartsWith("row_version :"));
        update.Split('\n').Should().Contain(l => l.TrimStart().StartsWith("Name :"));
    }
}
