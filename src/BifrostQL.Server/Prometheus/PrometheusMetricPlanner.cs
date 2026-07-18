using System;
using System.Collections.Generic;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Core.Utils;

namespace BifrostQL.Server.Prometheus
{
    /// <summary>
    /// One planned label dimension: <see cref="ResultKey"/> is the aggregate result-set
    /// column the group value is read back by (the model column's GraphQL name — the
    /// alias the grouped SQL projects), and <see cref="LabelName"/> is the deterministically
    /// normalized Prometheus label name it is exported under.
    /// </summary>
    public sealed record PrometheusLabelBinding(string ResultKey, string LabelName);

    /// <summary>
    /// The result of planning a Prometheus metric into an executable read intent: the
    /// programmatic <see cref="QueryIntent"/> (a grouped-aggregate <see cref="GqlObjectQuery"/>
    /// — never SQL text), the label bindings that map result columns back to exported label
    /// names, and the result-set keys for the count and sum projections.
    /// </summary>
    public sealed record PrometheusMetricPlan(
        PrometheusMetricConfig Config,
        QueryIntent Intent,
        IReadOnlyList<PrometheusLabelBinding> Labels,
        bool IncludesCount,
        string CountResultKey,
        string? SumResultKey);

    /// <summary>
    /// Translates a slice-1 <see cref="PrometheusMetricConfig"/> into a schema-derived
    /// grouped-aggregate <see cref="QueryIntent"/> that executes through the unskippable
    /// <see cref="IQueryIntentExecutor"/> pipeline — COUNT(*) and/or SUM(numeric column)
    /// GROUPED BY the metric's label columns. Every group/value column is a resolved model
    /// <see cref="ColumnDto"/>, never a client string, so no user-provided text is ever
    /// concatenated into SQL and the security transformer pass (tenant/soft-delete/policy)
    /// constrains the aggregate before grouping.
    ///
    /// <para>Aggregate shapes the grouped intent cannot express fail fast at plan time
    /// (which runs at metric-collection setup / startup) rather than falling back to hand-
    /// rolled SQL: a <c>COUNT(column)</c> non-null count and a missing/unresolvable
    /// column are rejected with <see cref="InvalidOperationException"/>.</para>
    /// </summary>
    public static class PrometheusMetricPlanner
    {
        /// <summary>Result-set alias for the SUM value projection (op group + column GraphQL name).</summary>
        internal const string SumOpGroup = "_sum";

        public static PrometheusMetricPlan Plan(
            PrometheusMetricConfig config,
            IDbTable table,
            string? endpoint,
            IDictionary<string, object?> userContext)
        {
            if (config is null) throw new ArgumentNullException(nameof(config));
            if (table is null) throw new ArgumentNullException(nameof(table));
            if (userContext is null) throw new ArgumentNullException(nameof(userContext));

            if (!config.DeclaresMetric)
                throw new InvalidOperationException(
                    $"Table '{table.TableSchema}.{table.DbName}' declares no Prometheus metric; nothing to plan.");

            // COUNT(*) is the only count the grouped-aggregate intent can express. A
            // COUNT(column) (non-null count of a specific column) would need a value
            // projection the grouped shape does not support — fail fast rather than
            // silently emit COUNT(*) or fall back to SQL text.
            if (config.CountColumn != null)
                throw new InvalidOperationException(
                    $"metric '{config.MetricName}' declares a column count ('{config.CountColumn}'); the grouped " +
                    "Prometheus aggregate supports only COUNT(*) (metric-count: enabled) — a per-column non-null " +
                    "count is not an expressible aggregate shape.");

            var includeCount = config.CountsAllRows;

            var labelBindings = new List<PrometheusLabelBinding>(config.Labels.Count);
            var groupColumns = new List<AggregateGroupColumn>(config.Labels.Count);
            foreach (var labelColumn in config.Labels)
            {
                var column = ResolveColumn(table, labelColumn, "label");
                // The grouped SQL projects `<dbCol> AS <GraphQlName>`; read the value back
                // by that alias, and export it under the deterministically normalized name.
                groupColumns.Add(new AggregateGroupColumn(column, column.GraphQlName));
                labelBindings.Add(new PrometheusLabelBinding(
                    column.GraphQlName, StringNormalizer.NormalizeName(column.DbName)));
            }

            string? sumResultKey = null;
            var valueColumns = new List<AggregateValueColumn>(1);
            if (config.SumColumn != null)
            {
                var column = ResolveColumn(table, config.SumColumn, "sum");
                sumResultKey = SumOpGroup + "_" + column.GraphQlName;
                valueColumns.Add(new AggregateValueColumn(
                    AggregateOperationType.Sum, column, SumOpGroup, sumResultKey));
            }

            if (!includeCount && valueColumns.Count == 0)
                throw new InvalidOperationException(
                    $"metric '{config.MetricName}' plans neither a COUNT(*) nor a SUM projection; " +
                    "a metric must count and/or sum something.");

            var grouped = new GroupedAggregate
            {
                GroupColumns = groupColumns,
                IncludeCount = includeCount,
                ValueColumns = valueColumns,
            };

            var query = new GqlObjectQuery
            {
                DbTable = table,
                TableName = table.DbName,
                SchemaName = table.TableSchema,
                GraphQlName = table.GraphQlName,
                FieldName = AggregateSurface.AggregateFieldName(table),
                GroupedAggregate = grouped,
            };

            var intent = new QueryIntent
            {
                Query = query,
                UserContext = userContext,
                Endpoint = endpoint,
            };

            return new PrometheusMetricPlan(
                config, intent, labelBindings, includeCount, GroupedAggregate.CountAlias, sumResultKey);
        }

        private static ColumnDto ResolveColumn(IDbTable table, string dbColumnName, string role)
        {
            // config canonicalizes column names to the table's DB casing; ColumnLookup is
            // keyed by DB name (case-insensitive). A miss means the model no longer carries
            // the column the metric names — fail fast, never hand-roll around it.
            if (!table.ColumnLookup.TryGetValue(dbColumnName, out var column))
                throw new InvalidOperationException(
                    $"metric on table '{table.TableSchema}.{table.DbName}' names {role} column " +
                    $"'{dbColumnName}', which does not exist on the resolved model.");
            return column;
        }
    }
}
