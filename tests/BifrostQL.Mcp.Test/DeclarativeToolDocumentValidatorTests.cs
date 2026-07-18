using System.Text;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Mcp.Test
{
    public sealed class DeclarativeToolDocumentValidatorTests
    {
        [Fact]
        public void Validate_AggregatesEveryReferenceError()
        {
            var document = ToolDocument(
                rootTable: "dbo.missing",
                rootFields: ["missing_root"],
                byId: "missingParam",
                enumDefault: "invalid");

            var errors = DeclarativeToolDocumentValidator.Validate(document, Model());

            errors.Should().HaveCount(3);
            errors.Should().Contain(error => error.Contains("params.detail.default") && error.Contains("invalid"));
            errors.Should().Contain(error => error.Contains("root.byId") && error.Contains("missingParam"));
            errors.Should().Contain(error => error.Contains("root.table") && error.Contains("dbo.missing"));
        }

        [Fact]
        public void Validate_RejectsUnknownIdParameterTableWithPrecisePath()
        {
            var document = ToolDocument(idTable: "dbo.missing");

            var errors = DeclarativeToolDocumentValidator.Validate(document, Model());

            errors.Should().ContainSingle()
                .Which.Should().Contain("Tool 'get_customer_context' at params.customerId.table")
                .And.Contain("dbo.missing");
        }

        [Fact]
        public void Validate_ReportsRootRelationFilterAndAggregatePaths()
        {
            var model = Model();
            var badRelation = ToolDocument(relation: "missing_relation");
            var badColumns = ToolDocument(rootFields: ["missing_root"], filterColumn: "missing_filter", aggregateColumn: "missing_total");

            var errors = DeclarativeToolDocumentValidator.Validate(badRelation, model)
                .Concat(DeclarativeToolDocumentValidator.Validate(badColumns, model)).ToList();

            errors.Should().Contain(error => error.Contains("root.fields") && error.Contains("missing_root"));
            errors.Should().Contain(error => error.Contains("include[0].relation") && error.Contains("missing_relation"));
            errors.Should().Contain(error => error.Contains("include[0].filter") && error.Contains("missing_filter"));
            errors.Should().Contain(error => error.Contains("include[0].aggregate.sum") && error.Contains("missing_total"));
        }

        [Fact]
        public async Task StartupValidation_UsesExecutorCachedModelAndAcceptsValidDocument()
        {
            var model = Model();
            var executor = new RecordingExecutor(model);
            var validator = new DeclarativeToolDocumentValidator(ToolDocument(), executor);

            await validator.StartAsync(CancellationToken.None);

            executor.GetModelCalls.Should().Be(1);
            executor.LastModel.Should().BeSameAs(model);
        }

        private static DeclarativeToolDocument ToolDocument(
            string rootTable = "dbo.customers",
            string idTable = "dbo.customers",
            string[]? rootFields = null,
            string byId = "customerId",
            string enumDefault = "summary",
            string relation = "orders",
            string filterColumn = "status",
            string aggregateColumn = "total")
        {
            var json = $$"""
                {
                  "version": 1,
                  "tools": [{
                    "name": "get_customer_context",
                    "description": "Return customer context for validation.",
                    "params": {
                      "customerId": { "type": "id", "table": "{{idTable}}" },
                      "detail": { "type": "enum", "values": ["summary", "full"], "default": "{{enumDefault}}" }
                    },
                    "root": { "table": "{{rootTable}}", "byId": "{{byId}}", "fields": {{JsonSerializer.Serialize(rootFields ?? ["id", "name"])}} },
                    "include": [{
                      "relation": "{{relation}}", "as": "orders",
                      "filter": { "{{filterColumn}}": { "_eq": "open" } },
                      "fields": ["id"], "aggregate": { "sum": "{{aggregateColumn}}" }
                    }]
                  }]
                }
                """;
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            return new DeclarativeToolDocumentLoader(
                new StreamDeclarativeToolDocumentSource(stream, "validator test")).Load();
        }

        private static IDbModel Model()
        {
            var customers = Table("customers", "id", "name");
            var orders = Table("orders", "id", "status", "total");
            customers.MultiLinks["orders"] = new TableLinkDto
            {
                Name = "orders", ParentTable = customers, ChildTable = orders,
                ParentId = customers.ColumnLookup["id"], ChildId = orders.ColumnLookup["id"],
            };
            return new DbModel
            {
                Tables = [customers, orders],
                Metadata = new Dictionary<string, object?>(),
            };
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

        private sealed class RecordingExecutor(IDbModel model) : IQueryIntentExecutor
        {
            public int GetModelCalls { get; private set; }
            public IDbModel? LastModel { get; private set; }

            public Task<IDbModel> GetModelAsync(string? endpoint = null)
            {
                GetModelCalls++;
                LastModel = model;
                return Task.FromResult(model);
            }

            public Task<QueryIntentResult> ExecuteAsync(QueryIntent intent, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
        }
    }
}
