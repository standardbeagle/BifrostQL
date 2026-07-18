using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Server.Prometheus;
using BifrostQL.Server.Test.OData;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Prometheus
{
    /// <summary>
    /// The planner turns a slice-1 <see cref="PrometheusMetricConfig"/> into a schema-derived
    /// grouped-aggregate intent — never SQL text — and fails fast on aggregate shapes the grouped
    /// intent cannot express, rather than falling back to hand-rolled SQL (criterion 1 + 3).
    /// </summary>
    public sealed class PrometheusMetricPlannerTests
    {
        private static readonly string[] Seed =
        {
            "CREATE TABLE Sales (id INTEGER PRIMARY KEY, region TEXT NOT NULL, product TEXT NOT NULL, amount REAL NOT NULL);",
            "INSERT INTO Sales(id, region, product, amount) VALUES (1, 'west', 'a', 1.0);",
        };

        private static async Task<IDbModel> ModelAsync(ODataRealDbHarness harness) => await harness.ModelAsync();

        [Fact]
        public async Task Plans_a_grouped_aggregate_intent_not_sql_text()
        {
            var meta = new[] { "main.Sales { metric-name: sales_total; metric-count: enabled; metric-sum: amount; metric-labels: region, product }" };
            await using var harness = await ODataRealDbHarness.StartAsync("plan-shape", meta, Seed);
            var table = (await ModelAsync(harness)).GetTableFromDbName("Sales");
            var config = PrometheusMetricConfig.FromTable(table);

            var plan = PrometheusMetricPlanner.Plan(config, table, ODataRealDbHarness.EndpointPath, new Dictionary<string, object?>());

            // Schema-derived programmatic intent: a GroupedAggregate on the query tree, no SQL string.
            plan.Intent.Query.GroupedAggregate.Should().NotBeNull();
            plan.Intent.Query.DbTable.Should().BeSameAs(table);
            plan.Intent.Endpoint.Should().Be(ODataRealDbHarness.EndpointPath);

            // Group columns are the resolved model columns, in declared order.
            plan.Intent.Query.GroupedAggregate!.GroupColumns.Select(g => g.Column.DbName)
                .Should().Equal("region", "product");
            plan.Intent.Query.GroupedAggregate.IncludeCount.Should().BeTrue();
            plan.Intent.Query.GroupedAggregate.ValueColumns.Select(v => v.Column.DbName).Should().Equal("amount");

            plan.IncludesCount.Should().BeTrue();
            plan.SumResultKey.Should().NotBeNull();
            plan.Labels.Select(l => l.LabelName).Should().Equal("region", "product");
        }

        [Fact]
        public async Task A_column_count_shape_fails_fast()
        {
            // metric-count naming a column = COUNT(column); the grouped intent supports only COUNT(*).
            var meta = new[] { "main.Sales { metric-name: sales_total; metric-count: amount; metric-labels: region }" };
            await using var harness = await ODataRealDbHarness.StartAsync("plan-colcount", meta, Seed);
            var table = (await ModelAsync(harness)).GetTableFromDbName("Sales");
            var config = PrometheusMetricConfig.FromTable(table);

            var act = () => PrometheusMetricPlanner.Plan(config, table, ODataRealDbHarness.EndpointPath, new Dictionary<string, object?>());

            act.Should().Throw<InvalidOperationException>().WithMessage("*column count*");
        }
    }
}
