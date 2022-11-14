using GraphQL.Resolvers;
using GraphQL;
using GraphQLParser.AST;
using GraphQLProxy.Model;
using System.Data.SqlClient;
using GraphQL.Types;
using GraphQL.Validation.Complexity;
using static GraphQLProxy.DbTableResolver;
using System.Drawing;

namespace GraphQLProxy
{
    public class DbTableResolver : IFieldResolver
    {
        private readonly IDbConnFactory _dbConnFactory;
        private readonly TableDto _table;
        public DbTableResolver(IDbConnFactory connFactory, TableDto table)
        {
            _dbConnFactory = connFactory;
            _table = table;
        }
        public async ValueTask<object?> ResolveAsync(IResolveFieldContext context)
        {
            if (context.SubFields == null)
                throw new ArgumentNullException(nameof(context) + ".SubFields");

            var tableSqlData = TableSqlData.From(context);
            var resultNames = tableSqlData.FullJoinNames.ToArray();

            using var conn = _dbConnFactory.GetConnection();
            await conn.OpenAsync();

            var command = new SqlCommand(tableSqlData.ToSql(), conn);
            using var reader = await command.ExecuteReaderAsync();
            var results = new List<(Dictionary<string, int> index, List<object?[]> data, string name)>();
            var resultIndex = 0;
            do
            {
                var resultName = resultNames[resultIndex++];
                var index = Enumerable.Range(0, reader.FieldCount).Select(i => (i, reader.GetName(i))).ToDictionary(x => x.Item2, x => x.i, StringComparer.OrdinalIgnoreCase);
                var result = new List<object?[]>();
                while (await reader.ReadAsync())
                {
                    var row = new object?[reader.FieldCount];
                    reader.GetValues(row);
                    result.Add(row);
                }
                if (result.Count == 0)
                    results.Add((index, new List<object?[]>(), resultName));
                else
                    results.Add((index, result, resultName));
            } while (await reader.NextResultAsync());
            return new ReaderEnum(results);
        }

        public sealed class TableFilter
        {
            public string? TableName { get; set; }
            public string ColumnName { get; set; }
            public string RelationName { get; set; }
            public object? Value { get; set; }

            public string ToSql(string? alias = null)
            {
                return DbFilterType.GetSingleFilter(alias ?? TableName, ColumnName, RelationName, Value);
            }
        }

        public sealed class TableJoin
        {
            public string Name { get; set; } = null!;
            public string? Alias { get; set; } = null!;
            public string JoinName => $"{Alias ?? Name}+{Name}";
            public string ParentColumn { get; set; } = null!;
            public string ChildColumn { get; set; } = null!;
            public JoinType JoinType { get; set; }
            public TableSqlData ParentTable { get; set; } = null!;
            public TableSqlData ChildTable { get; set; } = null!;

            public string GetParentSql()
            {
                if (ParentTable.ParentJoin == null)
                    return $"SELECT DISTINCT [{ParentColumn}] AS JoinId FROM [{ParentTable.TableName}]" + ParentTable.GetFilterSql();
                var baseSql = ParentTable.ParentJoin.GetParentSql();
                return $"SELECT DISTINCT a.[{ParentColumn}] AS JoinId FROM [{ParentTable.TableName}] a INNER JOIN ({baseSql}) b ON b.JoinId=a.[{ParentTable.ParentJoin.ChildColumn}]" + ParentTable.GetFilterSql("a");
            }

            public string GetSql()
            {
                var main = GetParentSql();
                var joinColumnSql = string.Join(",", ChildTable.FullColumnNames.Select(c => $"b.[{c.name}] AS [{c.alias}]"));

                var wrap = $"SELECT a.[JoinId] [src_id], {joinColumnSql} FROM ({main}) a";
                wrap += $" INNER JOIN [{ChildTable.TableName}] b ON a.[JoinId] = b.[{ChildColumn}]";

                var baseSql = wrap + ChildTable.GetFilterSql();
                if (ChildTable.Joins.Any() == false)
                    return baseSql;
                return baseSql + ";" + String.Join(";", ChildTable.Joins.Select(j => j.GetSql()));
            }
        }

        public enum JoinType
        {
            Join = 0,
            Single = 1,
        }

        public sealed class TableSqlData
        {
            public TableSqlData? Parent => ParentJoin?.ParentTable;
            public TableJoin? ParentJoin { get; set; }
            public string TableName { get; set; } = "";
            public List<string> ColumnNames { get; set; } = new List<string>();
            public List<string> Sort { get; set; } = new List<string>();
            public TableFilter? Filter { get; set; }
            public int? Limit { get; set; }
            public int? Offset { get; set; }

            public List<TableJoin> Joins { get; set; } = new List<TableJoin>();

            public IEnumerable<string> FullJoinNames => new[] { "base" }.Concat(Joins.SelectMany(j => j.ChildTable.FullJoinNames.Select(n => $"{j.JoinName}+{n}")));

            public IEnumerable<(string name, string alias)> FullColumnNames => ColumnNames.Select(c => (c, c))
                .Concat(Joins.Select(j => (j.ParentColumn, "key_" + j.JoinName)));

            public string GetFilterSql(string? alias = null)
            {
                if (Filter == null) return "";
                return " WHERE " + Filter.ToSql(alias);
            }

            public string ToSql()
            {
                var columnSql = String.Join(",", FullColumnNames.Select(n => $"[{n.name}] [{n.alias}]"));
                var cmdText = $"SELECT {columnSql} FROM [{TableName}]";

                var orderby = " ORDER BY (SELECT NULL)";
                if (Sort.Any())
                {
                    orderby = " ORDER BY " + String.Join(", ", Sort);
                }
                var limit = Limit != null ? $" FETCH NEXT {Limit} ROWS ONLY" : "";
                var offset = Offset != null ? $" OFFSET {Offset} ROWS" : " OFFSET 0 ROWS";

                var baseSql = cmdText + GetFilterSql() + orderby + offset + limit + ";";

                var joinSql = Joins.Select(tableJoin => tableJoin.GetSql());

                return baseSql + string.Join(";", joinSql);
            }
            public static TableSqlData From(IResolveFieldContext context)
            {
                var tableSqlData = GetBaseTable(context);

                var joinList = GetJoinList(context.FieldAst, context.FieldDefinition)
                    .Select(join => GetTableJoin(join, tableSqlData));
                tableSqlData.Joins.AddRange(joinList);
                return tableSqlData;
            }

            private static TableJoin GetTableJoin((GraphQLField Field, FieldType FieldType) join, TableSqlData tableSqlData)
            {
                var on = (join.Field.Arguments!).FirstOrDefault(arg => arg.Name == "on");
                var onFields = ((GraphQLListValue)on!.Value).Values!.Select(v => ((GraphQLStringValue)v).Value.ToString()).ToArray();

                var tableJoin = new TableJoin
                {
                    Name = join.Field.Name.StringValue,
                    Alias = join.Field.Alias?.Name.StringValue,
                    ParentColumn = onFields[0],
                    ChildColumn = onFields[1],
                    ParentTable = tableSqlData,
                    JoinType = join.Field.Name.StringValue.StartsWith("_join_") ? JoinType.Join : JoinType.Single,
                    ChildTable = new TableSqlData
                    {
                        TableName = join.Field.Name.StringValue.Replace("_join_", "").Replace("_single_", ""),
                        ColumnNames = GetColumnList(join.Field.SelectionSet!),
                        Filter = GetTableFilter(join, "b"),
                    }
                };
                tableJoin.ChildTable.ParentJoin = tableJoin;

                var joins = GetJoinList(join.Field, join.FieldType).Select(j => GetTableJoin(j, tableJoin.ChildTable));
                tableJoin.ChildTable.Joins.AddRange(joins);

                return tableJoin;
            }

            private static IEnumerable<(GraphQLField Field, FieldType FieldType)> GetJoinList(GraphQLField field, FieldType fieldType)
            {
                var resolvedType = fieldType.ResolvedType;
                if (resolvedType is ListGraphType listType) resolvedType = listType.ResolvedType;
                if (resolvedType == null || resolvedType is not ObjectGraphType) throw new ArgumentOutOfRangeException(nameof(fieldType));
                var objectType = (ObjectGraphType)resolvedType;
                return GetJoinList(field.SelectionSet!).Select(j => (j, objectType.GetField(j.Name)!)).ToArray();
            }

            private static TableSqlData GetBaseTable(IResolveFieldContext context)
            {
                string[] order = Array.Empty<string>();
                if (context.HasArgument("sort"))
                {
                    order = context.GetArgument<string[]>("sort");
                }

                List<string> columnList = GetColumnList(context.FieldAst.SelectionSet!);
                var tableSqlData = new TableSqlData
                {
                    TableName = context.FieldDefinition.Name,
                    ColumnNames = columnList,
                    Sort = order.ToList(),
                    Filter = GetTableFilter((Field: context.FieldAst, FieldType: context.FieldDefinition), null),
                    Limit = context.HasArgument("limit") ? context.GetArgument<int>("limit") : null,
                    Offset = context.HasArgument("offset") ? context.GetArgument<int>("offset") : null,
                };
                return tableSqlData;
            }

            private static List<string> GetColumnList(GraphQLSelectionSet selection)
            {
                return selection.Selections.Cast<GraphQLField>()
                    .Where(s => s.Name.StringValue.StartsWith("__") == false)
                    .Where(f => f.Name.StringValue.StartsWith("_join") == false)
                    .Where(f => f.Name.StringValue.StartsWith("_single") == false)
                    .Select(f => f.Name.StringValue)
                    .Distinct()
                    .ToList();
            }
            private static List<GraphQLField> GetJoinList(GraphQLSelectionSet selection)
            {
                return selection.Selections.Cast<GraphQLField>()
                    .Where(f => f.Name.StringValue.StartsWith("_join") == true || f.Name.StringValue.StartsWith("_single") == true)
                    .ToList();
            }

            private static TableFilter? GetTableFilter((GraphQLField Field, FieldType FieldType) join, string? tableName)
            {
                if ((join.Field.Arguments?.Any(arg => arg.Name == "filter") ?? false) == false)
                    return null;

                var filter = join.Field.Arguments?.First(arg => arg.Name == "filter");
                var filterValue = filter!.Value as GraphQLObjectValue;
                var field = filterValue!.Fields!.First() as GraphQLObjectField;
                var relation = (field.Value as GraphQLObjectValue)?.Fields?.First();

                var filterType = join.FieldType.Arguments?.First(arg => arg.Name == "filter").ResolvedType as InputObjectGraphType;
                var columnField = filterType!.GetField(field.Name);
                var filterFieldType = columnField!.ResolvedType as DbFilterType;
                var relationFieldType = filterFieldType!.GetField(relation!.Name);
                var relationType = relationFieldType!.ResolvedType as ScalarGraphType;
                var relationValue = relationType!.ParseLiteral(relation.Value);
                if (relation != null)
                {
                    return new TableFilter
                    {
                        TableName = tableName,
                        ColumnName = columnField.Name,
                        RelationName = relationFieldType.Name,
                        Value = relationValue,
                    };
                }

                return null;
            }
        }
    }
}
