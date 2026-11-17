using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.UI.NativeBridge;
using FluentAssertions;
using Xunit;

namespace BifrostQL.UI.Tests;

/// <summary>
/// <c>get-table-ddl</c> table identity. Before the fix the handler took the FIRST table whose
/// qualified name, bare DB name or GraphQL name matched, so with <c>dbo.users</c> and
/// <c>sales.users</c> both loaded, "Copy DDL" for the bare <c>users</c> the editor sent copied
/// whichever enumerated first. The fixture lists <c>dbo.users</c> first and gives each table a
/// distinct column so the assertion can tell which one answered.
/// </summary>
public sealed class SchemaDdlTableIdentityTests
{
    private static DbTable Table(string schema, string name, string column)
    {
        var columns = new[]
        {
            new ColumnDto { ColumnName = "id", GraphQlName = "id", DataType = "int", OrdinalPosition = 1, IsPrimaryKey = true },
            new ColumnDto { ColumnName = column, GraphQlName = column, DataType = "nvarchar", OrdinalPosition = 2 },
        };
        return new DbTable
        {
            DbName = name,
            GraphQlName = schema == "dbo" ? name : $"{schema}_{name}",
            NormalizedName = name,
            TableSchema = schema,
            TableType = "BASE TABLE",
            ColumnLookup = columns.ToDictionary(c => c.DbName, StringComparer.OrdinalIgnoreCase),
            GraphQlLookup = columns.ToDictionary(c => c.GraphQlName, StringComparer.OrdinalIgnoreCase),
        };
    }

    private static IDbModel Model() => new DbModel
    {
        Tables = [Table("dbo", "users", "email"), Table("sales", "users", "region"), Table("dbo", "orders", "total")],
        Metadata = new Dictionary<string, object?>(),
    };

    [Theory]
    [InlineData("sales.users", "sales", "region")]
    [InlineData("dbo.users", "dbo", "email")]
    [InlineData("SALES.USERS", "sales", "region")]
    public void A_schema_qualified_name_resolves_to_that_schemas_table(string requested, string schema, string column)
    {
        var table = SchemaDdlBridgeHandler.ResolveTable(Model(), requested);

        table.TableSchema.Should().Be(schema);
        table.ColumnLookup.Keys.Should().Contain(column);
    }

    [Fact]
    public void A_bare_name_one_schema_defines_still_resolves()
    {
        SchemaDdlBridgeHandler.ResolveTable(Model(), "orders").TableSchema.Should().Be("dbo");
    }

    [Fact]
    public void An_ambiguous_bare_name_fails_with_the_same_answer_as_an_unknown_name()
    {
        var model = Model();

        var ambiguous = () => SchemaDdlBridgeHandler.ResolveTable(model, "users");
        var unknown = () => SchemaDdlBridgeHandler.ResolveTable(model, "no_such_table");

        var ambiguousMessage = ambiguous.Should().Throw<BifrostExecutionError>().Which.Message;
        var unknownMessage = unknown.Should().Throw<BifrostExecutionError>().Which.Message;
        ambiguousMessage.Should().Be(unknownMessage);
        ambiguousMessage.Should().Be(SchemaDdlBridgeHandler.TableNotFoundMessage);
        ambiguousMessage.Should().NotContain("users", "the wire text names no table");
    }

    [Fact]
    public void A_graphql_name_is_not_a_ddl_name()
    {
        // The name space is DB names; the old MatchName arm let a GraphQL name through too.
        var act = () => SchemaDdlBridgeHandler.ResolveTable(Model(), "sales_users");
        act.Should().Throw<BifrostExecutionError>().Which.Message.Should().Be(SchemaDdlBridgeHandler.TableNotFoundMessage);
    }
}
