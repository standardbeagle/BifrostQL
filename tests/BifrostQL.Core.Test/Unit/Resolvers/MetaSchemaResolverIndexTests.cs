using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using FluentAssertions;

namespace BifrostQL.Core.Test.Unit.Resolvers;

/// <summary>
/// Pins the index projection emitted by the _dbSchema resolver. Clients use it
/// to choose a default sort an index can serve (13M rows sorted by an unindexed
/// column cost 8.7s PER PAGE); the projection must therefore report columns by
/// their GraphQL names — the names a client can actually put in a sort — and
/// must omit an index whose key includes a column the model does not expose,
/// because a partial column list would misrepresent the access path.
/// </summary>
public sealed class MetaSchemaResolverIndexTests
{
    private static JsonElement ResolveTable(IDbModel model, string table)
    {
        var resolver = new MetaSchemaResolver(model);
        var result = resolver.ResolveAsync(new NullArgContext()).AsTask().GetAwaiter().GetResult();

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var json = JsonSerializer.Serialize(result, options);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .First(t => t.GetProperty("graphQlName").GetString() == table)
            .Clone();
    }

    [Fact]
    public void Indexes_AreProjectedWithGraphQlColumnNames()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Claims", t =>
            {
                t.WithColumn("HCPCS CD", "varchar", graphQlName: "hCPCS_CD");
                t.WithColumn("year", "int");
                t.WithIndex("IX_Claims", isUnique: false, isClustered: true, isPrimaryKey: false, "HCPCS CD", "year");
            })
            .Build();

        var table = ResolveTable(model, "Claims");
        var index = table.GetProperty("indexes").EnumerateArray().Single();

        index.GetProperty("name").GetString().Should().Be("IX_Claims");
        index.GetProperty("isClustered").GetBoolean().Should().BeTrue();
        index.GetProperty("isUnique").GetBoolean().Should().BeFalse();
        index.GetProperty("isPrimaryKey").GetBoolean().Should().BeFalse();
        index.GetProperty("columns").EnumerateArray().Select(c => c.GetString())
            .Should().Equal("hCPCS_CD", "year");
    }

    [Fact]
    public void IndexReferencingAnUnexposedColumn_IsOmitted()
    {
        // The fixture table never exposes "ghost"; an index keyed on it cannot
        // be served by any sort the client is able to express.
        var model = DbModelTestFixture.Create()
            .WithTable("Claims", t =>
            {
                t.WithColumn("year", "int");
                t.WithIndex("IX_Ghost", isUnique: false, isClustered: false, isPrimaryKey: false, "year", "ghost");
                t.WithIndex("IX_Year", isUnique: false, isClustered: false, isPrimaryKey: false, "year");
            })
            .Build();

        var table = ResolveTable(model, "Claims");
        table.GetProperty("indexes").EnumerateArray()
            .Select(ix => ix.GetProperty("name").GetString())
            .Should().Equal("IX_Year");
    }

    [Fact]
    public void TableWithoutIndexes_ProjectsAnEmptyList()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Plain", t => t.WithPrimaryKey("Id"))
            .Build();

        var table = ResolveTable(model, "Plain");
        table.GetProperty("indexes").EnumerateArray().Should().BeEmpty();
    }

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
