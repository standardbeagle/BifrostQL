using GraphQL.Resolvers;
using GraphQL;
using GraphQLParser.AST;
using GraphQLProxy.Model;
using System.Data.SqlClient;
using GraphQL.Types;

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
            Dictionary<string, (GraphQLField Field, FieldType FieldType)> subFields = context.SubFields;
            List<string> columnList = GetColumnList(context.FieldAst.SelectionSet!);

            var joinList = subFields
                .Where(f => f.Value.FieldType.Name.StartsWith("_join"))
                .ToList();

            var (cmdText, resultNames) = GetSqlText(context, columnList, joinList);

            using var conn = _dbConnFactory.GetConnection();
            await conn.OpenAsync();

            var command = new SqlCommand(cmdText, conn);
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

        private static List<string> GetColumnList(GraphQLSelectionSet selection)
        {
            return selection.Selections.Cast<GraphQLField>()
                .Where(s => s.Name.StringValue.StartsWith("__") == false)
                .Where(f => f.Name.StringValue.StartsWith("_join") == false)
                .Select(f => f.Name.StringValue)
                .Distinct()
                .ToList();
        }

        private static (string sql, string[] resultNames) GetSqlText(IResolveFieldContext context, List<string> columnList, List<KeyValuePair<string, (GraphQLField Field, FieldType FieldType)>> joinList)
        {
            var orderby = " ORDER BY (SELECT NULL)";
            if (context.HasArgument("sort"))
            {
                var order = context.GetArgument<string[]>("sort");
                orderby = " ORDER BY " + String.Join(", ", order);
            }
            var limit = context.HasArgument("limit") ? $" FETCH NEXT {context.GetArgument<int>("limit")} ROWS ONLY" : "";
            var offset = context.HasArgument("offset") ? $" OFFSET {context.GetArgument<int>("offset")} ROWS" : " OFFSET 0 ROWS";

            var filterText = "";
            if (context.HasArgument("filter"))
            {
                filterText += " WHERE";
                var colFilter = context.GetArgument<Dictionary<string, object?>>("filter");
                filterText += $"{DbFilterType.GetSingleFilter(colFilter.First())}";
            }

            var joinNames = new List<string>() { "base" };
            var joins = new List<string>();
            var fullColumnList = new List<(string name, string alias)>();
            fullColumnList.AddRange(columnList.Select(n => (n, n)));
            foreach (var join in joinList)
            {
                string name = join.Value.FieldType.Name;
                var joinName = $"{join.Key}+{name}";
                joinNames.Add(joinName);
                var on = (join.Value.Field.Arguments!).FirstOrDefault(arg => arg.Name == "on");
                var onFields = ((GraphQLListValue)on!.Value).Values!.Select(v => ((GraphQLStringValue)v).Value.ToString()).ToArray();

                fullColumnList.Add((onFields[0], "key_" + joinName));
                var main = $"SELECT [{onFields[0]}] FROM [{context.FieldDefinition.Name}]" + filterText;
                var joinColumnList = GetColumnList(join.Value.Field.SelectionSet!);
                var joinColumnSql = string.Join(",", joinColumnList.Select(c => $"b.[{c}]"));
                var wrap = $"SELECT a.[{onFields[0]}] [src_id], {joinColumnSql} FROM ({main}) a";
                var joinText = $" INNER JOIN [{name.Replace("_join_", "")}] b ON a.[{onFields[0]}] = b.[{onFields[1]}]";
                //if (join.Value.Field.Arguments!.Any(arg => arg.Name == "filter")) {
                //    var filter = join.Value.Field.Arguments?.First(arg => arg.Name == "filter");
                //    var filterValue = filter.GetPropertyValue(typeof(IDictionary<string, object?>));
                //}
                joins.Add(wrap + joinText);
            }

            var columnSql = String.Join(",", fullColumnList.Select(n => $"[{n.name}] [{n.alias}]"));
            var cmdText = $"SELECT {columnSql} FROM [{context.FieldDefinition.Name}]";

            var result = cmdText + filterText + orderby + offset + limit + ";" + String.Join(";", joins);

            return (result, joinNames.ToArray());
        }
    }

}
