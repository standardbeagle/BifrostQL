using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.Sqlite;
using BifrostQL.SqlServer;
using FluentAssertions;

namespace BifrostQL.Core.Test.Unit.Resolvers;

/// <summary>
/// Pins the dbColumnSchema.isLargeValue projection: it must come from the MODEL'S
/// dialect type mapper, not a type-name lookup, so the same declared type ("text")
/// projects differently per dialect. edit-db uses this flag to decide which columns
/// are excluded from grid SELECTs and fetched on demand — a wrong true hides real
/// data from the grid (the SQLite every-string-column-blank bug).
/// </summary>
public sealed class MetaSchemaResolverLargeValueTests
{
    private static Dictionary<string, bool> ResolveColumnFlags(IDbModel model, string tableName)
    {
        var resolver = new MetaSchemaResolver(model);
        var result = resolver.ResolveAsync(new NullArgContext()).AsTask().GetAwaiter().GetResult();

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var json = JsonSerializer.Serialize(result, options);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .First(t => t.GetProperty("graphQlName").GetString() == tableName)
            .GetProperty("columns").EnumerateArray()
            .ToDictionary(
                c => c.GetProperty("graphQlName").GetString()!,
                c => c.GetProperty("isLargeValue").GetBoolean());
    }

    private static IDbModel BuildDocsModel(ITypeMapper? typeMapper = null)
    {
        var fixture = DbModelTestFixture.Create()
            .WithTable("Docs", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Title", "varchar")
                .WithColumn("Body", "text")
                .WithColumn("Payload", "blob"));
        if (typeMapper != null)
            fixture = fixture.WithTypeMapper(typeMapper);
        return fixture.Build();
    }

    [Fact]
    public void SqlServerMapper_ProjectsTextAsLarge()
    {
        var model = BuildDocsModel(SqlServerTypeMapper.Instance);

        var flags = ResolveColumnFlags(model, "Docs");

        flags["Title"].Should().BeFalse();
        flags["Body"].Should().BeTrue("SQL Server text is a LOB type");
    }

    [Fact]
    public void SqliteMapper_ProjectsTextAsOrdinaryString()
    {
        var model = BuildDocsModel(SqliteTypeMapper.Instance);

        var flags = ResolveColumnFlags(model, "Docs");

        flags["Body"].Should().BeFalse("SQLite TEXT is the database's only string type");
        flags["Payload"].Should().BeTrue("BLOB affinity keeps fetch-on-demand semantics");
    }

    [Fact]
    public void DefaultAnsiMapper_TreatsAmbiguousTextAsOrdinary()
    {
        var model = BuildDocsModel(); // fixture default: AnsiSqlTypeMapper

        var flags = ResolveColumnFlags(model, "Docs");

        flags["Body"].Should().BeFalse("without dialect knowledge, hiding data is the worse failure");
        flags["Payload"].Should().BeTrue("blob is an unambiguous LOB in every dialect");
    }

    /// <summary>Minimal context: the resolver only reads the graphQlName argument.</summary>
    private sealed class NullArgContext : IBifrostFieldContext
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
}
