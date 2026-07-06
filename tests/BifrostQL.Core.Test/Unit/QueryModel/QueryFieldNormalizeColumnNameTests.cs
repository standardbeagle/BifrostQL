using System.Collections.Generic;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.QueryModel
{
    /// <summary>
    /// Regression coverage for the query-type marker stripping in
    /// <c>QueryField.NormalizeColumnName</c>. The marker (<c>_join_</c>,
    /// <c>_single_</c>, <c>_agg</c>) is only meaningful as a LEADING prefix. A
    /// blanket <c>string.Replace</c> corrupted any name that merely CONTAINED a
    /// marker — e.g. <c>order_aggregates</c> collapsed to <c>orderregates</c>
    /// because "_agg" appears mid-string — breaking the table lookup.
    /// </summary>
    public sealed class QueryFieldNormalizeColumnNameTests
    {
        [Fact]
        public void NameContainingMarkerMidString_IsPreserved()
        {
            var model = DbModelTestFixture.Create()
                .WithTable("order_aggregates", t => t
                    .WithColumn("id", "int", isPrimaryKey: true)
                    .WithColumn("total", "int"))
                .Build();

            var field = new QueryField
            {
                Name = "order_aggregates",
                Fields = new List<IQueryField>
                {
                    new QueryField { Name = "id" },
                },
            };

            var result = field.ToSqlData(model);

            // With the old blanket Replace, FieldName became "orderregates" and the
            // table lookup (keyed by the same normalization) threw before we got here.
            result.FieldName.Should().Be("order_aggregates");
            result.GraphQlName.Should().Be("order_aggregates");
        }

        [Fact]
        public void LeadingMarker_IsStripped()
        {
            var model = DbModelTestFixture.Create()
                .WithTable("orders", t => t
                    .WithColumn("id", "int", isPrimaryKey: true))
                .Build();

            // A `_join_`-prefixed field name normalizes to the bare table name; a
            // mid-string occurrence of "orders" must not be touched.
            var field = new QueryField
            {
                Name = "_join_orders",
                Fields = new List<IQueryField> { new QueryField { Name = "id" } },
            };

            var result = field.ToSqlData(model);

            result.FieldName.Should().Be("orders");
        }
    }
}
