using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using GraphQL.Types;
using GraphQL.Utilities;
using Microsoft.Data.Sqlite;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// Finding M10 acceptance test (schema-level, closed enumeration): walk the
/// generated SDL and execute one document per advertised root/relationship field
/// shape. Every advertised shape must execute — an advertised-but-unexecutable
/// field (the bare <c>_join</c>/<c>_single</c> dynamic-join containers, whose
/// <c>on: [String!]</c> argument shape <c>QueryField.ToJoin</c> cannot consume)
/// fails this walk.
/// </summary>
public sealed class AdvertisedSchemaShapeTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_advertised_shape_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;
    private ISchema _schema = null!;
    private SqliteDbConnFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("PRAGMA foreign_keys = ON");
        await Exec("DROP TABLE IF EXISTS posts");
        await Exec("DROP TABLE IF EXISTS blogs");
        await Exec("CREATE TABLE blogs (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");
        await Exec("""
            CREATE TABLE posts (
                id INTEGER PRIMARY KEY,
                blog_id INTEGER NOT NULL,
                title TEXT NOT NULL,
                FOREIGN KEY (blog_id) REFERENCES blogs(id)
            )
            """);
        await Exec("INSERT INTO blogs(id, name) VALUES (1, 'b1'), (2, 'b2')");
        await Exec("INSERT INTO posts(id, blog_id, title) VALUES (10, 1, 'p1'), (11, 2, 'p2')");

        _factory = new SqliteDbConnFactory(ConnString);
        var loader = new DbModelLoader(_factory, new MetadataLoader(Array.Empty<string>()));
        _model = await loader.LoadAsync();
        _schema = DbSchema.FromModel(_model);
        _schema.Initialize();
    }

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    [Fact]
    public void GeneratedSdl_AdvertisesNoDynamicJoinContainers()
    {
        var sdl = _schema.Print();
        sdl.Should().NotContain("_join :", "the bare _join container cannot execute (M10)");
        sdl.Should().NotContain("_single :", "the bare _single container cannot execute (M10)");
        sdl.Should().NotContain("_join {", "no <T>_join container type may remain in the SDL");
        sdl.Should().NotContain("_single {", "no <T>_single container type may remain in the SDL");
    }

    [Fact]
    public async Task EveryAdvertisedRootFieldShape_Executes()
    {
        var executed = new List<string>();
        foreach (var field in _schema.Query!.Fields)
        {
            var document = BuildFieldDocument(field, 0, out var reason);
            if (document is null)
                throw new InvalidOperationException($"Root field '{field.Name}' shape cannot be exercised: {reason}");
            await ExecuteAndAssertNoErrors(document, $"root field '{field.Name}'");
            executed.Add(field.Name);
        }

        // Closed enumeration: the fixture schema advertises exactly these root
        // shapes — table paged reads, grouped aggregates, pivots, and _dbSchema.
        executed.Should().Contain(new[] { "blogs", "posts", "blogsAggregate", "postsAggregate", "blogsPivot", "postsPivot", "_dbSchema" });
    }

    [Fact]
    public async Task EveryAdvertisedRowTypeObjectField_Executes()
    {
        var executed = new List<string>();
        foreach (var table in _model.Tables)
        {
            var rowType = _schema.AllTypes[table.GraphQlName] as IObjectGraphType;
            rowType.Should().NotBeNull($"row type '{table.GraphQlName}' must exist in the schema");
            var rootField = _schema.Query!.Fields.FirstOrDefault(f => f.Name == table.GraphQlName);
            rootField.Should().NotBeNull();

            foreach (var field in rowType!.Fields)
            {
                if (Named(field.ResolvedType) is not IComplexGraphType)
                    continue; // scalar/enum column fields execute trivially with the root read

                var selection = MinimalSelection(field, 0, out var reason);
                if (selection is null)
                    throw new InvalidOperationException($"Field '{table.GraphQlName}.{field.Name}' shape cannot be exercised: {reason}");
                var document = $"{{ {table.GraphQlName} {{ data {{ {field.Name}{selection} }} }} }}";
                await ExecuteAndAssertNoErrors(document, $"relationship field '{table.GraphQlName}.{field.Name}'");
                executed.Add($"{table.GraphQlName}.{field.Name}");
            }
        }

        // Closed enumeration: FK-derived relationship shapes on the row types —
        // single-link (posts.blog) and multi-link (blogs.posts) — plus the _agg
        // column aggregate. No other object-typed field may be advertised.
        executed.Should().Contain(new[] { "blogs._agg", "blogs.posts", "posts._agg", "posts.blog" });
        executed.Should().HaveCount(4, "every advertised relationship shape is enumerated above; a new shape requires a deliberate fixture update");
    }

    private async Task ExecuteAndAssertNoErrors(string document, string subject)
    {
        var execution = await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = _schema;
            options.Query = document;
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = _factory,
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, _schema, BifrostQL.Core.Modules.NullQueryTransformerService.Instance),
            });
        });

        execution.Errors.Should().BeNullOrEmpty(
            $"the SDL advertises {subject}, so it must execute; document: {document}");
    }

    /// <summary>
    /// Builds a root-field document <c>{ field(args) selection }</c>, filling every
    /// required argument from the schema's own types (first enum value, literal for
    /// scalars, recursive literal for input objects).
    /// </summary>
    private static string? BuildFieldDocument(FieldType field, int depth, out string? reason)
    {
        var args = FillArguments(field, depth, out reason);
        if (args is null)
            return null;
        var selection = MinimalSelection(field, depth, out reason);
        if (selection is null && reason != null)
            return null;
        reason = null;
        return $"{{ {field.Name}{args}{selection} }}";
    }

    private static string? FillArguments(FieldType field, int depth, out string? reason)
    {
        reason = null;
        if (field.Arguments is null)
            return string.Empty;
        var parts = new List<string>();
        foreach (var arg in field.Arguments)
        {
            if (arg.ResolvedType is not NonNullGraphType nonNull || arg.DefaultValue is not null)
                continue; // optional — the client may narrow, never must
            var literal = LiteralFor(nonNull.ResolvedType!, depth, out reason);
            if (literal is null)
                return null;
            parts.Add($"{arg.Name}: {literal}");
        }
        return parts.Count == 0 ? string.Empty : $"({string.Join(", ", parts)})";
    }

    private static string? LiteralFor(IGraphType type, int depth, out string? reason)
    {
        reason = null;
        switch (type)
        {
            case NonNullGraphType nn:
                return LiteralFor(nn.ResolvedType!, depth, out reason);
            case ListGraphType list:
                var inner = LiteralFor(list.ResolvedType!, depth, out reason);
                return inner is null ? null : $"[{inner}]";
            case EnumerationGraphType enumeration:
                var values = enumeration.Values?.Select(v => v.Name).ToList() ?? new List<string>();
                if (values.Count == 0)
                {
                    reason = "enum with no values";
                    return null;
                }
                return values.FirstOrDefault(v => v.Equals("count", StringComparison.OrdinalIgnoreCase)) ?? values[0];
            case IInputObjectGraphType input when depth < 3:
                var first = input.Fields.FirstOrDefault();
                if (first is null)
                {
                    reason = "input object with no fields";
                    return null;
                }
                var leaf = LiteralFor(first.ResolvedType!, depth + 1, out reason);
                return leaf is null ? null : $"{{ {first.Name}: {leaf} }}";
            case ScalarGraphType scalar:
                return scalar.Name switch
                {
                    "String" or "ID" => "\"x\"",
                    "Int" => "1",
                    "Boolean" => "true",
                    _ => nullWithReason(out reason, $"cannot synthesize a literal for scalar '{scalar.Name}'"),
                };
            default:
                reason = $"cannot synthesize a literal for '{type}'";
                return null;
        }

        static string? nullWithReason(out string? r, string message) { r = message; return null; }
    }

    /// <summary>
    /// Minimal sub-selection for an object/list field: scalar or enum leaves when
    /// the target has any, otherwise the first object field recursively.
    /// </summary>
    private static string? MinimalSelection(FieldType field, int depth, out string? reason)
    {
        reason = null;
        if (Named(field.ResolvedType) is not IComplexGraphType complex)
            return null; // scalar/enum field — no sub-selection
        return SelectionFor(complex, depth, out reason);
    }

    private static string? SelectionFor(IComplexGraphType type, int depth, out string? reason)
    {
        reason = null;
        // A grouped-aggregate row must select an aggregate datum (_count) — group
        // keys alone are a refused shape — so prefer it over plain columns.
        var leaves = type.Fields
            .Where(f => Named(f.ResolvedType) is not IComplexGraphType)
            .OrderByDescending(f => f.Name == "_count")
            .Take(2)
            .ToList();
        if (leaves.Count > 0)
            return $"{{ {string.Join(" ", leaves.Select(f => f.Name))} }}";
        if (depth >= 3)
        {
            reason = $"no scalar leaf within depth bound on '{type.Name}'";
            return null;
        }
        var child = type.Fields.FirstOrDefault();
        if (child is null)
        {
            reason = $"object type '{type.Name}' has no fields";
            return null;
        }
        var nested = SelectionFor((IComplexGraphType)Named(child.ResolvedType)!, depth + 1, out reason);
        return nested is null ? null : $"{{ {child.Name}{nested} }}";
    }

    private static IGraphType? Named(IGraphType? type) => type switch
    {
        null => null,
        NonNullGraphType nonNull => Named(nonNull.ResolvedType),
        ListGraphType list => Named(list.ResolvedType),
        _ => type,
    };
}
