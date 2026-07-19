using System.Reflection;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// FTS slice 1: the <c>_search</c> operator is surfaced as a TABLE-level filter argument
/// only for tables that declare searchable columns. Unlike every existing operator (which
/// is COLUMN-scoped and emitted per <c>FilterType…Input</c>), <c>_search</c> spans several
/// columns, so it belongs on the table's filter input — getting this wrong would surface
/// <c>_search</c> on every scalar column of every table.
/// </summary>
public class FtsSchemaSurfacingTests
{
    private static readonly MethodInfo SchemaTextFromModelMethod = typeof(DbSchema).Assembly
        .GetType("BifrostQL.Core.Schema.SchemaGenerator")!
        .GetMethod("SchemaTextFromModel", BindingFlags.Static | BindingFlags.Public)!;

    private static string SchemaText(IDbModel model) =>
        (string)SchemaTextFromModelMethod.Invoke(null, new object[] { model, true })!;

    // articles(id, title, body) with title/body declared searchable.
    private static IDbModel BuildSearchableModel() =>
        DbModelTestFixture.Create()
            .WithTable("articles", t => t
                .WithPrimaryKey("id")
                .WithColumn("title", "nvarchar")
                .WithColumn("body", "text")
                .WithMetadata(MetadataKeys.Fts.Search, "title,body"))
            .Build();

    // Same shape, no search metadata.
    private static IDbModel BuildNonSearchableModel() =>
        DbModelTestFixture.Create()
            .WithTable("articles", t => t
                .WithPrimaryKey("id")
                .WithColumn("title", "nvarchar")
                .WithColumn("body", "text"))
            .Build();

    private static string TypeBlock(string sdl, string header)
    {
        var start = sdl.IndexOf(header, System.StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"expected '{header}' in SDL");
        var end = sdl.IndexOf('}', start);
        return sdl.Substring(start, end - start);
    }

    [Fact]
    public void SearchableTable_SurfacesSearchOnTableFilterInput()
    {
        var sdl = SchemaText(BuildSearchableModel());

        TypeBlock(sdl, "input TableFilterarticlesInput {")
            .Should().Contain("_search : String");
    }

    [Fact]
    public void NonSearchableTable_TableFilterInputHasNoSearch()
    {
        var sdl = SchemaText(BuildNonSearchableModel());

        TypeBlock(sdl, "input TableFilterarticlesInput {")
            .Should().NotContain("_search");
    }

    [Fact]
    public void Search_DoesNotAppearOnPerColumnFilterTypeInput()
    {
        // _search must never leak onto a per-column FilterType…Input, or it would appear
        // on every scalar column of every table.
        var sdl = SchemaText(BuildSearchableModel());

        TypeBlock(sdl, "input FilterTypeStringInput {")
            .Should().NotContain("_search");
    }
}
