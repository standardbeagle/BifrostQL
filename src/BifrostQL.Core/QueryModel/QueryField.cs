using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.ComputedColumns;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;

namespace BifrostQL.Core.QueryModel
{
    public interface IQueryField
    {
        string? Alias { get; init; }
        string Name { get; init; }
        object? Value { get; set; }
        List<IQueryField> Fields { get; init; }
        List<QueryArgument> Arguments { get; init; }
        List<string> Fragments { get; init; }
        string ToString();
        GqlObjectQuery ToSqlData(IDbModel model, IQueryField? parent = null, string basePath = "");
        TableJoin ToJoin(IDbModel model, GqlObjectQuery parent);
        FieldType Type { get; }
        GqlObjectColumn ToScalarSql(IDbTable dbTable);
        GqlAggregateColumn ToAggregateSql(IDbTable dbTable);


    }

    public enum FieldType
    {
        Scalar,
        Join,
        Link,
        Aggregate,
        System,
    }

    public sealed class QueryField : IQueryField
    {
        public string? Alias { get; init; }
        public string Name { get; init; } = null!;
        public string RefName => Alias ?? Name;
        public object? Value { get; set; }
        public bool IncludeResult { get; set; }
        public List<IQueryField> Fields { get; init; } = new();
        public List<QueryArgument> Arguments { get; init; } = new();
        public List<string> Fragments { get; init; } = new();
        public override string ToString() => $"{Alias}:{Name}={Value}({Arguments.Count})/{Fields.Count}/{Fragments.Count}";

        public FieldType Type => this switch
        {
            { Fields: var fields, Name: var n } when fields.Any() && IsSpecialColumn(n) => FieldType.Join,
            { Fields: var fields, Name: var n } when fields.Any() && IsSpecialColumn(n) == false => FieldType.Link,
            { Name: "_agg" } => FieldType.Aggregate,
            { Name: var n } when n.StartsWith("__") => FieldType.System,
            _ => FieldType.Scalar,
        };

        public string GetUniqueName()
        {
            return Alias ?? Name;
        }

        public GqlObjectQuery ToSqlData(IDbModel model, IQueryField? parent = null, string basePath = "")
        {
            var path = string.IsNullOrWhiteSpace(basePath)
                switch
            { true => GetUniqueName(), false => basePath + "->" + GetUniqueName() };
            var tableName = ResolveTableName(model, parent, Name);
            var dbTable = model.GetTableByFullGraphQlName(tableName);
            var rawSort = (IEnumerable<object?>?)Arguments.FirstOrDefault(a => a.Name == "sort")?.Value;
            var sort = rawSort?.Cast<string>()?.ToList() ?? new List<string>();

            // Nested multi-link collections now carry the same paged contract as
            // top-level queries: a `<child>_paged` wrapper with a `data` selection
            // plus `total/offset/limit`. Flagging IncludeResult here makes the
            // child node emit a per-parent total and routes its `data` sub-selection
            // through the same unwrapping the top-level query uses below.
            // Single-links keep their bare object shape; many-to-many keep arrays.
            if (parent != null && IsMultiLink(model, parent, Name))
                IncludeResult = true;

            var dataFields = Fields.FirstOrDefault(f => f.Name == "data")?.Fields ?? new List<IQueryField>();
            var queryFields = (IncludeResult ? dataFields : Fields);
            var standardFields = queryFields.Where(f => f.Type != FieldType.System).ToList();
            var queryType = GetQueryType(Name);
            if (queryType == QueryType.Aggregate)
            {
                var agg = Arguments.FirstOrDefault(a => a.Name == "operation")?.Value?.ToString() ?? throw new BifrostExecutionError("Aggregate query missing operation argument.");
                var aggType = (AggregateOperationType)Enum.Parse(typeof(AggregateOperationType), agg, true);
                var value = Arguments.FirstOrDefault(a => a.Name == "value")?.Value?.ToString() ?? throw new BifrostExecutionError("Aggregate query missing value argument.");
            }

            var result = new GqlObjectQuery
            {
                Alias = Alias,
                DbTable = dbTable,
                TableName = dbTable.DbName,
                SchemaName = dbTable.TableSchema,
                FieldName = NormalizeColumnName(Name),
                GraphQlName = tableName,
                Path = path,
                QueryType = queryType,
                IsFragment = false,
                IncludeResult = IncludeResult,
                ScalarColumns = standardFields.Where(f => f.Type == FieldType.Scalar).Select(f => f.ToScalarSql(dbTable)).ToList(),
                AggregateColumns = standardFields.Where(f => f.Type == FieldType.Aggregate).Select(f => f.ToAggregateSql(dbTable)).ToList(),
                Sort = sort,
                Limit = (int?)Arguments.FirstOrDefault(a => a.Name == "limit")?.Value,
                Offset = (int?)Arguments.FirstOrDefault(a => a.Name == "offset")?.Value,
                Filter = BuildCombinedFilter(Arguments, dbTable),
                Links = standardFields
                            .Where((f) => f.Type == FieldType.Link)
                            .Select(f => f.ToSqlData(model, this, path))
                            .ToList(),
            };
            result.Joins.AddRange(
                standardFields
                    .Where((f) => f.Type == FieldType.Join)
                    .Select(f => f.ToJoin(model, result))
                );
            if (parent == null)
                result.ConnectLinks(model);
            return result;
        }

        private static bool IsMultiLink(IDbModel model, IQueryField parent, string fieldName)
        {
            var normalizedFieldName = NormalizeColumnName(fieldName);
            var parentTable = model.GetTableByFullGraphQlName(NormalizeColumnName(parent.Name));
            // A single-link sharing the field name (self-referential FK) takes
            // precedence — it stays a bare object, so don't treat it as paged.
            if (parentTable.SingleLinks.TryGetValue(normalizedFieldName, out _)
                || parentTable.SingleLinks.Values.Any(l => string.Equals(l.ParentFieldName, normalizedFieldName, StringComparison.OrdinalIgnoreCase)))
                return false;
            if (parentTable.MultiLinks.TryGetValue(normalizedFieldName, out _)
                || parentTable.MultiLinks.Values.Any(l => string.Equals(l.ChildFieldName, normalizedFieldName, StringComparison.OrdinalIgnoreCase)))
                return true;
            // Many-to-many target collections page per source parent exactly like
            // multi-links (the junction hop is transparent to the contract).
            return parentTable.ManyToManyLinks.TryGetValue(normalizedFieldName, out _)
                || parentTable.ManyToManyLinks.Values.Any(l => string.Equals(l.TargetTable.GraphQlName, normalizedFieldName, StringComparison.OrdinalIgnoreCase));
        }

        private static string ResolveTableName(IDbModel model, IQueryField? parent, string fieldName)
        {
            var normalizedFieldName = NormalizeColumnName(fieldName);
            if (parent == null)
                return normalizedFieldName;

            var parentTable = model.GetTableByFullGraphQlName(NormalizeColumnName(parent.Name));
            if (parentTable.SingleLinks.TryGetValue(normalizedFieldName, out var singleLink)
                || (singleLink = parentTable.SingleLinks.Values.FirstOrDefault(l => string.Equals(l.ParentFieldName, normalizedFieldName, StringComparison.OrdinalIgnoreCase))) != null)
                return singleLink.ParentTable.GraphQlName;

            if (parentTable.MultiLinks.TryGetValue(normalizedFieldName, out var multiLink)
                || (multiLink = parentTable.MultiLinks.Values.FirstOrDefault(l => string.Equals(l.ChildFieldName, normalizedFieldName, StringComparison.OrdinalIgnoreCase))) != null)
                return multiLink.ChildTable.GraphQlName;

            return normalizedFieldName;
        }

        public TableJoin ToJoin(IDbModel model, GqlObjectQuery parent)
        {
            var onArg = Arguments.FirstOrDefault(a => a.Name == "on");

            if (onArg == null)
                throw new BifrostExecutionError($"join on table {parent.GraphQlName} missing on argument.");

            var columns = (onArg.Value as IDictionary<string, object?>) ?? throw new BifrostExecutionError($"While joining table {parent.GraphQlName}, unable to convert on value to object");
            if (columns.Keys.Count != 1)
                throw new ArgumentException("on joins only support one column per table");
            var relation = columns.Values.First() as IDictionary<string, object?> ?? throw new BifrostExecutionError($"While joining table {parent.GraphQlName}, unable to convert on value to a string");
            return new TableJoin
            {
                Name = Name,
                Alias = Alias,
                FromTable = parent,
                ConnectedTable = ToSqlData(model),
                FromColumn = columns.Keys.First(),
                ConnectedColumn = relation.Values?.First()?.ToString() ?? throw new BifrostExecutionError($"While joining table {parent.GraphQlName}, unable to resolve join column {relation?.Keys?.FirstOrDefault()}"),
                Operator = relation.Keys.First(),
                QueryType = GetQueryType(Name),
            };
        }

        public GqlObjectColumn ToScalarSql(IDbTable dbTable)
        {
            var computed = ComputedColumnConfigCollector.Find(dbTable, Name);
            if (computed != null)
                return new GqlObjectColumn(computed, RefName);

            return new GqlObjectColumn(dbTable.GraphQlLookup[Name].DbName, RefName);
        }


        public GqlAggregateColumn ToAggregateSql(IDbTable dbTable)
        {
            var agg = Arguments.FirstOrDefault(a => a.Name == "operation")?.Value?.ToString() ?? throw new BifrostExecutionError("Aggregate query missing operation argument.");
            var aggType = (AggregateOperationType)Enum.Parse(typeof(AggregateOperationType), agg, true);

            var links = new List<(LinkDirection direction, TableLinkDto link)>();
            var value = Arguments.FirstOrDefault(a => a.Name == "value")?.Value;
            var currentTable = dbTable;
            while (value is IDictionary<string, object?> objVal)
            {
                if (objVal.Keys.Count != 1)
                    throw new BifrostExecutionError("Aggregations only support one join per aggregation");
                var linkName = objVal.Keys.First();
                if (linkName == "column")
                {
                    value = objVal.Values.First();
                    continue;
                }

                var matched = false;
                if (currentTable.SingleLinks.TryGetValue(linkName, out var singleLink)
                    || (singleLink = currentTable.SingleLinks.Values.FirstOrDefault(l => string.Equals(l.ParentFieldName, linkName, StringComparison.OrdinalIgnoreCase))) != null)
                {
                    links.Add((LinkDirection.ManyToOne, singleLink));
                    currentTable = singleLink.ParentTable;
                    matched = true;
                }

                if (!matched && (currentTable.MultiLinks.TryGetValue(linkName, out var multiLink)
                    || (multiLink = currentTable.MultiLinks.Values.FirstOrDefault(l => string.Equals(l.ChildFieldName, linkName, StringComparison.OrdinalIgnoreCase))) != null)
                )
                {
                    links.Add((LinkDirection.OneToMany, multiLink));
                    currentTable = multiLink.ChildTable;
                    matched = true;
                }
                if (!matched)
                    throw new BifrostExecutionError($"Unable to find link {linkName} on table {currentTable.GraphQlName}");

                value = objVal.Values.First();
            }
            var aggColumn = value?.ToString() ?? throw new BifrostExecutionError("Aggregate query value must be a column enum.");



            return new GqlAggregateColumn(links, currentTable.GraphQlLookup[aggColumn].DbName, GetUniqueName(), aggType);

        }

        public static void SyncFieldFragments(IQueryField queryField, IDictionary<string, IQueryField> fragmentList)
        {
            foreach (var fragmentField in queryField.Fragments.Select(f => fragmentList[f]).SelectMany(f => f.Fields))
            {
                queryField.Fields.Add(CopyField(fragmentField));
            }
            foreach (var subField in queryField.Fields)
            {
                SyncFieldFragments(subField, fragmentList);
            }
        }

        public static IQueryField CopyField(IQueryField queryField)
        {
            return new QueryField()
            {
                Name = queryField.Name,
                Alias = queryField.Alias,
                Value = queryField.Value,
                Arguments = queryField.Arguments,
                Fields = queryField.Fields.Select(CopyField).ToList(),
                Fragments = queryField.Fragments,
            };
        }

        private static readonly (string, QueryType)[] ColumnTypeMap = new[]
        {
            ("_join_", QueryType.Join),
            ("_single_", QueryType.Single),
            ("_agg", QueryType.Aggregate),
        };

        private static string NormalizeColumnName(string name)
        {
            var result = name;
            foreach (var (prefix, _) in ColumnTypeMap)
            {
                result = result.Replace(prefix, "");
            }
            return result;
        }

        private static bool IsSpecialColumn(string name)
        {
            foreach (var (prefix, _) in ColumnTypeMap)
            {
                if (name.StartsWith(prefix))
                    return true;
            }
            return false;
        }

        private static QueryType GetQueryType(string name)
        {
            foreach (var (prefix, type) in ColumnTypeMap)
            {
                if (name.StartsWith(prefix))
                    return type;
            }
            return QueryType.Standard;
        }

        private static TableFilter? BuildCombinedFilter(List<QueryArgument> arguments, IDbTable dbTable)
        {
            var filterArg = arguments.FirstOrDefault(a => a is { Name: "filter", Value: not null });
            var pkArg = arguments.FirstOrDefault(a => a is { Name: "_primaryKey", Value: not null });

            TableFilter? filterResult = filterArg != null
                ? TableFilter.FromObject(filterArg.Value, dbTable.DbName)
                : null;

            TableFilter? pkResult = null;
            if (pkArg?.Value is IEnumerable<object?> pkValues)
            {
                pkResult = TableFilter.FromPrimaryKey(pkValues, dbTable.KeyColumns, dbTable.DbName);
            }

            if (filterResult != null && pkResult != null)
            {
                return new TableFilter
                {
                    And = new List<TableFilter> { filterResult, pkResult },
                    FilterType = FilterType.And,
                };
            }

            return filterResult ?? pkResult;
        }
    }
}
