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
/// Pins the enum value/label projection emitted by the _dbSchema resolver.
/// Labels map to values positionally, so a count mismatch must drop the labels
/// rather than shift every label onto the wrong value.
/// </summary>
public sealed class MetaSchemaResolverEnumTests
{
    private static JsonElement ResolveColumn(IDbModel model, string table, string column)
    {
        var resolver = new MetaSchemaResolver(model);
        var result = resolver.ResolveAsync(new NullArgContext()).AsTask().GetAwaiter().GetResult();

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var json = JsonSerializer.Serialize(result, options);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .First(t => t.GetProperty("graphQlName").GetString() == table)
            .GetProperty("columns").EnumerateArray()
            .First(c => c.GetProperty("dbName").GetString() == column)
            .Clone();
    }

    private static IDbModel ModelWith(string values, string? labels)
    {
        var builder = DbModelTestFixture.Create()
            .WithTable("Widgets", t =>
            {
                t.WithPrimaryKey("Id");
                t.WithColumn("Status", "nvarchar");
                t.WithColumnMetadata("Status", MetadataKeys.Enum.Values, values);
                if (labels != null)
                    t.WithColumnMetadata("Status", MetadataKeys.Enum.Labels, labels);
            });
        return builder.Build();
    }

    [Fact]
    public void MatchingCounts_EmitsLabels()
    {
        var column = ResolveColumn(ModelWith("a,b,c", "Alpha,Bravo,Charlie"), "Widgets", "Status");

        column.GetProperty("enumValues").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("a", "b", "c");
        column.GetProperty("enumLabels").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("Alpha", "Bravo", "Charlie");
    }

    [Fact]
    public void MismatchedCounts_DropsLabels()
    {
        var column = ResolveColumn(ModelWith("a,b,c", "Alpha,Bravo"), "Widgets", "Status");

        // Values still emitted; labels dropped so the client shows raw values
        // rather than mislabelling (Bravo would otherwise land on "c").
        column.GetProperty("enumValues").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("a", "b", "c");
        column.GetProperty("enumLabels").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void NoLabels_EmitsNullLabels()
    {
        var column = ResolveColumn(ModelWith("a,b", labels: null), "Widgets", "Status");

        column.GetProperty("enumLabels").ValueKind.Should().Be(JsonValueKind.Null);
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
