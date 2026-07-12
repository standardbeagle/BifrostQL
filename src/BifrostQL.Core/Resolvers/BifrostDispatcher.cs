using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.ComputedColumns;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Schema;
using BifrostQL.Core.Storage;
using GraphQL;
using GraphQL.Resolvers;
using GraphQL.Types;
using GraphQL.Utilities;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// Universal resolver dispatcher that routes field resolution based on DbModel.
    /// Builds a resolver map from the model and attaches itself as the IFieldResolver
    /// on every field in the schema, delegating to the appropriate IBifrostResolver.
    ///
    /// Also serves as the entry point for protocol-agnostic IBifrostRequest processing.
    /// Any frontend can produce IBifrostRequest intents and convert them to GqlObjectQuery
    /// via <see cref="ToObjectQueries"/>, bypassing the GraphQL AST entirely.
    /// </summary>
    public sealed class BifrostDispatcher : IBifrostResolver, IFieldResolver
    {
        private readonly IDbModel _model;
        private readonly Dictionary<(string typeName, string fieldName), IBifrostResolver> _resolvers;

        public BifrostDispatcher(IDbModel model)
        {
            _model = model;
            _resolvers = BuildResolverMap(model);
        }

        /// <summary>
        /// Converts protocol-agnostic request intents into GqlObjectQuery objects
        /// suitable for the SQL generation pipeline. This is the bridge between
        /// any IProtocolFrontend's parsed output and the existing SQL engine.
        ///
        /// When an IBifrostRequest has a pre-built Filter (e.g., from a non-GraphQL frontend),
        /// it is merged into the generated GqlObjectQuery's filter chain.
        /// </summary>
        public IReadOnlyList<GqlObjectQuery> ToObjectQueries(IReadOnlyList<IBifrostRequest> requests)
        {
            var queries = new List<GqlObjectQuery>(requests.Count);
            foreach (var request in requests)
            {
                var queryField = BifrostRequestAdapter.ToQueryField(request);
                var query = queryField.ToSqlData(_model);
                ApplyPreBuiltFilters(request, query);
                queries.Add(query);
            }
            return queries;
        }

        /// <summary>
        /// Applies pre-built filters from IBifrostRequest to the generated GqlObjectQuery.
        /// This supports non-GraphQL frontends that construct TableFilter directly
        /// instead of passing filter dictionaries through Arguments.
        /// </summary>
        private static void ApplyPreBuiltFilters(IBifrostRequest request, GqlObjectQuery query)
        {
            if (request.Filter == null) return;

            if (query.Filter == null)
            {
                query.Filter = request.Filter;
            }
            else
            {
                query.Filter = new TableFilter
                {
                    And = new List<TableFilter> { query.Filter, request.Filter },
                    FilterType = FilterType.And,
                };
            }
        }

        public ValueTask<object?> ResolveAsync(IBifrostFieldContext context)
        {
            return DbJoinFieldResolver.Instance.ResolveAsync(context);
        }

        async ValueTask<object?> IFieldResolver.ResolveAsync(IResolveFieldContext context)
        {
            var parentTypeName = context.ParentType.Name;
            var fieldName = context.FieldDefinition.Name;

            try
            {
                if (_resolvers.TryGetValue((parentTypeName, fieldName), out var resolver))
                {
                    if (resolver is IFieldResolver fieldResolver)
                        return await fieldResolver.ResolveAsync(context);
                    return await resolver.ResolveAsync(new BifrostFieldContextAdapter(context));
                }

                return await DbJoinFieldResolver.Instance.ResolveAsync(new BifrostFieldContextAdapter(context));
            }
            catch (BifrostExecutionError ex)
            {
                throw new ExecutionError(ex.Message, ex);
            }
        }

        /// <summary>
        /// Wires this dispatcher as the resolver for all fields via the SchemaBuilder API.
        /// Must be called inside the Schema.For callback (before schema initialization).
        /// </summary>
        public void WireResolvers(SchemaBuilder builder)
        {
            const string queryType = "database";
            const string mutationType = "databaseInput";

            var query = builder.Types.For(queryType);
            var mut = builder.Types.For(mutationType);

            var historyTargets = Schema.HistorySurface.ResolveTargets(_model);

            foreach (var table in _model.Tables)
            {
                // History targets are system tables: SchemaGenerator emits no root
                // query/mutation/aggregate/pivot field for them, so there is nothing
                // to wire. Their TYPE fields (columns, _agg) below ARE wired — the
                // `<table>History` trail fields resolve rows of that type.
                if (!historyTargets.Contains(table))
                {
                    query.FieldFor(table.GraphQlName).Resolver = this;
                    mut.FieldFor(table.GraphQlName).Resolver = this;
                    mut.FieldFor($"{table.GraphQlName}_batch").Resolver = this;

                    WireAggregateResolvers(builder, table);
                    WirePivotResolver(builder, table);
                }

                var tableType = builder.Types.For(table.GraphQlName);
                tableType.FieldFor("_agg").Resolver = this;

                WireHistoryResolver(builder, table);

                foreach (var column in table.Columns)
                    tableType.FieldFor(column.GraphQlName).Resolver = this;

                foreach (var column in ComputedColumnConfigCollector.FromTable(table, _model))
                    tableType.FieldFor(column.Name).Resolver = this;

                foreach (var singleLink in table.SingleLinks)
                {
                    // Enum-FK single-links are not emitted to the schema (the FK
                    // column surfaces as the enum scalar instead), so there is no
                    // navigation field to wire — mirror TableSchemaGenerator via the
                    // shared EnumColumnMap.IsEnumLink predicate.
                    if (_model.EnumColumns != null
                        && _model.EnumColumns.IsEnumLink(table.DbName, singleLink.Value))
                        continue;
                    tableType.FieldFor(singleLink.Value.ParentFieldName).Resolver = this;
                }

                foreach (var multiLink in table.MultiLinks)
                    tableType.FieldFor(multiLink.Value.ChildFieldName).Resolver = this;

                foreach (var m2mLink in table.ManyToManyLinks)
                    tableType.FieldFor(m2mLink.Value.TargetTable.GraphQlName).Resolver = this;

                // Previously this method also looped over every table pair
                // and wired `_join_<table>` / `_single_<table>` resolvers.
                // The schema generator never emits those per-pair fields
                // (TableSchemaGenerator.cs:91-97 keeps the per-pair codegen
                // commented out and only emits the bare `_single`/`_join`
                // fields), so the loop wrote to orphan field configs that
                // no GraphQL operation ever reached. Removed.
                //
                // If per-pair `_join_<table>`/`_single_<table>` fields are
                // wanted again, restore the schema codegen *and* add this
                // loop back together — see docs/research/agg-dialect-survey.md
                // for the wiring contract.
            }

            query.FieldFor("_dbSchema").Resolver = this;

            if (SchemaGenerator.IsRawSqlEnabled(_model))
                query.FieldFor("_rawQuery").Resolver = this;

            if (SchemaGenerator.IsGenericTableEnabled(_model))
            {
                query.FieldFor("_table").Resolver = this;

                var genericResultType = builder.Types.For("GenericTableResult");
                foreach (var fieldName in new[] { "tableName", "columns", "rows", "totalCount" })
                    genericResultType.FieldFor(fieldName).Resolver = this;

                var genericColumnType = builder.Types.For("GenericColumnMetadata");
                foreach (var fieldName in new[] { "name", "dataType", "isNullable", "isPrimaryKey" })
                    genericColumnType.FieldFor(fieldName).Resolver = this;
            }

            if (FileStorageSchemaExtensions.IsFileStorageEnabled(_model))
            {
                query.FieldFor("_fileDownload").Resolver = this;
                mut.FieldFor("_fileUpload").Resolver = this;
                mut.FieldFor("_fileDelete").Resolver = this;
            }

            foreach (var proc in _model.StoredProcedures)
            {
                if (proc.IsReadOnly)
                    query.FieldFor(proc.FullGraphQlName).Resolver = this;
                else
                    mut.FieldFor(proc.FullGraphQlName).Resolver = this;

                var resultType = builder.Types.For(proc.ResultTypeName);
                foreach (var fieldName in new[] { "resultSets", "affectedRows" })
                    resultType.FieldFor(fieldName).Resolver = this;

                foreach (var outputParam in proc.OutputParameters)
                    resultType.FieldFor(outputParam.GraphQlName).Resolver = this;
            }
        }

        /// <summary>
        /// Wires the GROUP BY aggregate surface for one table: the root
        /// <c>&lt;table&gt;Aggregate</c> field to a dedicated
        /// <see cref="AggregateTableResolver"/>, and every field on the aggregate
        /// output types to the shared <see cref="AggregateFieldResolver"/>. These
        /// bypass the join dispatcher because their sources are plain aggregate-row
        /// objects, not the row/lookup readers <see cref="DbJoinFieldResolver"/> reads.
        /// The value op groups and the aggregate-values type exist only when the table
        /// has a numeric column — mirroring <see cref="TableSchemaGenerator"/>.
        /// </summary>
        private void WireAggregateResolvers(SchemaBuilder builder, IDbTable table)
        {
            const string queryType = "database";
            builder.Types.For(queryType).FieldFor(AggregateSurface.AggregateFieldName(table)).Resolver =
                new AggregateTableResolver(table);

            var rowType = builder.Types.For(AggregateSurface.AggregateRowTypeName(table));
            foreach (var column in AggregateSurface.GroupableColumns(table))
                rowType.FieldFor(column.GraphQlName).Resolver = AggregateFieldResolver.Instance;
            rowType.FieldFor(AggregateSurface.CountField).Resolver = AggregateFieldResolver.Instance;

            var numericColumns = AggregateSurface.NumericColumns(table, _model.TypeMapper).ToList();
            if (numericColumns.Count == 0)
                return;

            foreach (var (opGroup, _) in AggregateSurface.ValueOps)
                rowType.FieldFor(opGroup).Resolver = AggregateFieldResolver.Instance;

            var fieldsType = builder.Types.For(AggregateSurface.AggregateFieldsTypeName(table));
            foreach (var column in numericColumns)
                fieldsType.FieldFor(column.GraphQlName).Resolver = AggregateFieldResolver.Instance;
        }

        /// <summary>
        /// Wires the PIVOT surface for one table: the root <c>&lt;table&gt;Pivot</c>
        /// field to a dedicated <see cref="PivotTableResolver"/>. The field returns the
        /// JSON scalar (dynamic pivot columns), so there are no per-field output-type
        /// resolvers to wire — the scalar serializes the resolver's payload directly.
        /// </summary>
        private void WirePivotResolver(SchemaBuilder builder, IDbTable table)
        {
            const string queryType = "database";
            builder.Types.For(queryType).FieldFor(Schema.PivotSurface.PivotFieldName(table)).Resolver =
                new PivotTableResolver(table);
        }

        /// <summary>
        /// Wires the trail read surface for one table: the root
        /// <c>&lt;table&gt;History</c> field to a dedicated <see cref="HistoryTableResolver"/>.
        /// Wired ONLY when <see cref="Schema.HistorySurface.ResolveReadTarget"/> resolves —
        /// the identical condition <see cref="Schema.TableSchemaGenerator"/> emits the
        /// field under, so the SDL and the wiring cannot drift. The target's row/paged
        /// types need no extra wiring: the history table is an ordinary published table
        /// whose type fields are already dispatched above.
        /// </summary>
        private void WireHistoryResolver(SchemaBuilder builder, IDbTable table)
        {
            var target = Schema.HistorySurface.ResolveReadTarget(_model, table);
            if (target is null)
                return;

            const string queryType = "database";
            builder.Types.For(queryType).FieldFor(Schema.HistorySurface.HistoryFieldName(table)).Resolver =
                new HistoryTableResolver(table, target);
        }

        private static Dictionary<(string typeName, string fieldName), IBifrostResolver> BuildResolverMap(IDbModel model)
        {
            var map = new Dictionary<(string, string), IBifrostResolver>();

            const string queryType = "database";
            const string mutationType = "databaseInput";

            var historyTargets = Schema.HistorySurface.ResolveTargets(model);
            foreach (var table in model.Tables)
            {
                // No root fields exist for history targets (system tables), so no
                // root resolvers either — mirroring SchemaGenerator/WireResolvers.
                if (historyTargets.Contains(table))
                    continue;
                map[(queryType, table.GraphQlName)] = new DbTableResolver(table);
                map[(mutationType, table.GraphQlName)] = new DbTableMutateResolver(table);
                map[(mutationType, $"{table.GraphQlName}_batch")] = new DbTableBatchResolver(table);
            }

            map[(queryType, "_dbSchema")] = new MetaSchemaResolver(model);

            if (SchemaGenerator.IsRawSqlEnabled(model))
                map[(queryType, "_rawQuery")] = new RawSqlQueryResolver(model);

            if (SchemaGenerator.IsGenericTableEnabled(model))
            {
                var config = GenericTableConfig.FromModel(model);
                map[(queryType, "_table")] = new GenericTableQueryResolver(model, config);
            }

            if (FileStorageSchemaExtensions.IsFileStorageEnabled(model))
            {
                var fileStorageService = new Storage.FileStorageService();
                map[(queryType, "_fileDownload")] = new FileDownloadResolver(fileStorageService);
                map[(mutationType, "_fileUpload")] = new FileUploadResolver(fileStorageService);
                map[(mutationType, "_fileDelete")] = new FileDeleteResolver(fileStorageService);
            }

            foreach (var proc in model.StoredProcedures)
            {
                var resolver = new StoredProcedureResolver(proc);
                if (proc.IsReadOnly)
                    map[(queryType, proc.FullGraphQlName)] = resolver;
                else
                    map[(mutationType, proc.FullGraphQlName)] = resolver;
            }

            return map;
        }
    }
}
