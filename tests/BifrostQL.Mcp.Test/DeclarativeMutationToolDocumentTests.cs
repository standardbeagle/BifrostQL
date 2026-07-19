using System.Text;
using System.Text.Json;
using BifrostQL.Core.Model;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Mcp.Test
{
    /// <summary>
    /// Loader (shape) and validator (model) coverage for the declared WRITE tool DSL
    /// (<c>mutation</c>): a tool is EITHER a read root OR a write mutation, actions are
    /// constrained, insert/update require values, update/delete require a byId param,
    /// and every value column + <c>$param</c> reference is checked against the model.
    /// </summary>
    public sealed class DeclarativeMutationToolDocumentTests
    {
        private static DeclarativeToolDocument Load(string json) =>
            new DeclarativeToolDocumentLoader(
                new StreamDeclarativeToolDocumentSource(new MemoryStream(Encoding.UTF8.GetBytes(json)), "test")).Load();

        [Fact]
        public void Loader_AcceptsValidInsertMutation()
        {
            var document = Load("""
                {
                  "version": 1,
                  "tools": [{
                    "name": "create_ticket", "description": "Create a support ticket.",
                    "params": { "subject": { "type": "string", "description": "s" } },
                    "mutation": { "table": "dbo.tickets", "action": "insert", "values": { "subject": "$subject", "status": "open" } }
                  }]
                }
                """);

            var tool = document.Tools.Should().ContainSingle().Subject;
            tool.IsMutation.Should().BeTrue();
            tool.Mutation!.Action.Should().Be("insert");
            tool.Mutation.Values.Should().ContainKey("subject").And.ContainKey("status");
        }

        [Fact]
        public void Loader_RejectsToolDeclaringBothRootAndMutation()
        {
            var act = () => Load("""
                {
                  "version": 1,
                  "tools": [{
                    "name": "hybrid", "description": "Not allowed to be both.",
                    "root": { "table": "dbo.tickets", "byId": "id", "fields": ["id"] },
                    "mutation": { "table": "dbo.tickets", "action": "insert", "values": { "id": "$id" } }
                  }]
                }
                """);

            act.Should().Throw<InvalidOperationException>().WithMessage("*must not also declare 'root'*");
        }

        [Fact]
        public void Loader_RejectsInsertWithById()
        {
            var act = () => Load("""
                {
                  "version": 1,
                  "tools": [{
                    "name": "bad_insert", "description": "Insert cannot address a row.",
                    "params": { "id": { "type": "id", "description": "id" } },
                    "mutation": { "table": "dbo.tickets", "action": "insert", "byId": "id", "values": { "subject": "$id" } }
                  }]
                }
                """);

            act.Should().Throw<InvalidOperationException>().WithMessage("*insert mutation must not declare 'byId'*");
        }

        [Fact]
        public void Loader_RejectsDeleteWithValues()
        {
            var act = () => Load("""
                {
                  "version": 1,
                  "tools": [{
                    "name": "bad_delete", "description": "Delete carries no values.",
                    "params": { "id": { "type": "id", "description": "id" } },
                    "mutation": { "table": "dbo.tickets", "action": "delete", "byId": "id", "values": { "x": 1 } }
                  }]
                }
                """);

            act.Should().Throw<InvalidOperationException>().WithMessage("*delete mutation must not declare 'values'*");
        }

        [Fact]
        public void Validator_FlagsUnknownColumnAndUndeclaredParameterAndBadByIdType()
        {
            var document = Load("""
                {
                  "version": 1,
                  "tools": [{
                    "name": "rename_order", "description": "Rename an order row.",
                    "params": { "orderId": { "type": "string", "description": "wrong type" }, "name": { "type": "string", "description": "n" } },
                    "mutation": { "table": "dbo.orders", "action": "update", "byId": "orderId", "values": { "ghost": "$missing" } }
                  }]
                }
                """);

            var errors = DeclarativeToolDocumentValidator.Validate(document, Model());

            errors.Should().Contain(e => e.Contains("mutation.values") && e.Contains("ghost"));
            errors.Should().Contain(e => e.Contains("mutation.values.ghost") && e.Contains("$missing"));
            errors.Should().Contain(e => e.Contains("mutation.byId") && e.Contains("must have type 'id'"));
        }

        [Fact]
        public void Validator_FlagsUnknownMutationTable()
        {
            var document = Load("""
                {
                  "version": 1,
                  "tools": [{
                    "name": "make_ghost", "description": "Targets a table not in the model.",
                    "params": { "x": { "type": "string", "description": "x" } },
                    "mutation": { "table": "dbo.ghosts", "action": "insert", "values": { "x": "$x" } }
                  }]
                }
                """);

            DeclarativeToolDocumentValidator.Validate(document, Model())
                .Should().ContainSingle().Which.Should().Contain("mutation.table").And.Contain("dbo.ghosts");
        }

        private static IDbModel Model()
        {
            var orders = Table("orders", "id", "name", "status");
            return new DbModel { Tables = [orders], Metadata = new Dictionary<string, object?>() };
        }

        private static DbTable Table(string name, params string[] columnNames)
        {
            var columns = columnNames.Select((column, index) => new ColumnDto
            {
                TableCatalog = "test", TableSchema = "dbo", TableName = name,
                ColumnName = column, GraphQlName = column, NormalizedName = column,
                ColumnRef = new ColumnRef("test", "dbo", name, column), DataType = "text",
                OrdinalPosition = index + 1, IsPrimaryKey = column == "id",
            }).ToArray();
            return new DbTable
            {
                DbName = name, GraphQlName = name, NormalizedName = name,
                TableSchema = "dbo", TableType = "BASE TABLE",
                ColumnLookup = columns.ToDictionary(column => column.ColumnName, StringComparer.OrdinalIgnoreCase),
                GraphQlLookup = columns.ToDictionary(column => column.GraphQlName, StringComparer.OrdinalIgnoreCase),
            };
        }
    }
}
