using BifrostQL.Core.Model;

namespace BifrostQL.Core.Schema
{
    /// <summary>The GraphQL namespace a generated name lives in.</summary>
    internal enum GeneratedNameSpace
    {
        /// <summary>A named type: object, input, or enum.</summary>
        Type,
        /// <summary>A field on the root query type (<c>database</c>).</summary>
        QueryField,
        /// <summary>A field on the root mutation type (<c>databaseInput</c>).</summary>
        MutationField,
    }

    /// <summary>One name the schema generator emits for a table.</summary>
    internal readonly record struct GeneratedName(GeneratedNameSpace Space, string Name);

    /// <summary>
    /// Every type and root field name <see cref="SchemaGenerator"/> emits for one table, so
    /// <c>ModelConfigValidator</c> can refuse a model whose tables generate the same name twice
    /// (tables <c>orders</c> and <c>orders_aggregate</c> both define <c>type orders_aggregate</c>;
    /// <c>posts</c> and <c>posts_batch</c> both define the mutation field <c>posts_batch</c>).
    /// GraphQL.NET would otherwise fail the schema build with no table named.
    ///
    /// <para>The names come from the members the emitters call (<see cref="IDbTable"/>'s name
    /// properties, <see cref="AggregateSurface"/>, <see cref="PivotSurface"/>,
    /// <see cref="HistorySurface"/>, <see cref="TableSchemaGenerator"/>'s input type names), under
    /// the same conditions <see cref="SchemaGenerator"/> emits them.
    /// <c>GeneratedSchemaNamesTests</c> parses a generated SDL and fails when it defines a
    /// per-table name missing here.</para>
    /// </summary>
    internal static class GeneratedSchemaNames
    {
        public static IEnumerable<GeneratedName> For(IDbModel model, IDbTable table, IReadOnlySet<IDbTable> historyTargets)
        {
            var name = table.GraphQlName;

            // Emitted for every table, history targets included (the trail field uses them).
            yield return Type(name);
            yield return Type($"{name}_paged");
            yield return Type(AggregateSurface.AggregateRowTypeName(table));
            if (AggregateSurface.NumericColumns(table, model.TypeMapper).Any())
                yield return Type(AggregateSurface.AggregateFieldsTypeName(table));
            yield return Type(table.TableFilterTypeName);
            yield return Type(table.ColumnFilterTypeName);
            yield return Type(table.AggregateValueTypeName);
            yield return Type(table.ColumnEnumTypeName);
            yield return Type(table.TableColumnSortEnumName);

            if (model.EnumColumns is { } enumColumns
                && enumColumns.EnumTables.ContainsKey(EnumTableKey.Of(table))
                && EnumTableConfig.FromTable(table) is { } enumConfig)
            {
                yield return Type(enumConfig.GraphQlEnumName);
                yield return Type(EnumTableSchemaGenerator.FilterTypeNameFor(enumConfig));
            }

            if (HistorySurface.ResolveReadTarget(model, table) is not null)
                yield return new GeneratedName(GeneratedNameSpace.QueryField, HistorySurface.HistoryFieldName(table));

            // History targets take no root query field and no writes.
            if (historyTargets.Contains(table))
                yield break;

            yield return new GeneratedName(GeneratedNameSpace.QueryField, name);
            yield return new GeneratedName(GeneratedNameSpace.QueryField, AggregateSurface.AggregateFieldName(table));
            yield return new GeneratedName(GeneratedNameSpace.QueryField, PivotSurface.PivotFieldName(table));

            yield return new GeneratedName(GeneratedNameSpace.MutationField, name);
            yield return new GeneratedName(GeneratedNameSpace.MutationField, TableSchemaGenerator.BatchFieldName(table));

            foreach (var action in new[] { MutateActions.Insert, MutateActions.Update, MutateActions.Upsert, MutateActions.Delete })
                yield return Type(table.GetActionTypeName(action));
            yield return Type(TableSchemaGenerator.BatchInputTypeName(table));
            yield return Type(TableSchemaGenerator.DeltaTypeNameFor(table));
            yield return Type(TableSchemaGenerator.NestedSyncInsertTypeNameFor(table));
            yield return Type(TableSchemaGenerator.NestedSaveTypeNameFor(table));
            if (Modules.FilteredUpdateConfig.IsEnabled(table))
            {
                yield return Type(TableSchemaGenerator.SetTypeNameFor(table));
                yield return Type(TableSchemaGenerator.UpdateWhereTypeNameFor(table));
            }
        }

        private static GeneratedName Type(string name) => new(GeneratedNameSpace.Type, name);
    }
}
