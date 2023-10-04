using BifrostQL.Core.Model;
using GraphQL;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
    }

    public sealed class QueryField : IQueryField
    {
        public string? Alias { get; init; }
        public string Name { get; init; } = null!;
        public object? Value { get; set; }
        public bool IncludeResult { get; set; }
        public List<IQueryField> Fields { get; init; } = new();
        public List<QueryArgument> Arguments { get; init; } = new();
        public List<string> Fragments { get; init; } = new();
        public override string ToString() => $"{Alias}:{Name}={Value}({Arguments.Count})/{Fields.Count}/{Fragments.Count}";

        public string GetUniqueName()
        {
            return Alias ?? Name;
        }

        public GqlObjectQuery ToSqlData(IDbModel model, IQueryField? parent = null, string basePath = "")
        {
            var path = string.IsNullOrWhiteSpace(basePath) 
                switch { true => GetUniqueName(), false => basePath + "->" + GetUniqueName() };
            var tableName = NormalizeColumnName(Name);
            var dbTable = model.GetTableByFullGraphQlName(tableName);
            var rawSort = (IEnumerable<object?>?)Arguments.FirstOrDefault(a => a.Name == "sort")?.Value;
            var sort = rawSort?.Cast<string>()?.ToList() ?? new List<string>();
            var dataFields = Fields.FirstOrDefault(f => f.Name == "data")?.Fields ?? new List<IQueryField>();
            var standardFields = (IncludeResult ? dataFields : Fields).Where(f => !f.Name.StartsWith("__")).ToList();
            var result = new GqlObjectQuery
            {
                Alias = Alias,
                TableName = dbTable.DbName,
                SchemaName = dbTable.TableSchema,
                GraphQlName = tableName,
                Path = path,
                IsFragment = false,
                IncludeResult = IncludeResult,
                ScalarColumns = standardFields.Where(f => f.Fields.Any() == false).Select(f => (f.Name, dbTable.GraphQlLookup[f.Name].DbName)).ToList(),
                Sort = sort,
                Limit = (int?)Arguments.FirstOrDefault(a => a.Name == "limit")?.Value,
                Offset = (int?)Arguments.FirstOrDefault(a => a.Name == "offset")?.Value,
                Filter = Arguments.Where(a => a is { Name: "filter", Value: not null }).Select(arg => TableFilter.FromObject(arg.Value, dbTable.DbName)).FirstOrDefault(),
                Links = standardFields
                            .Where((f) => f.Fields.Any() && IsSpecialColumn(f.Name) == false)
                            .Select(f => f.ToSqlData(model, this, path))
                            .ToList(),
            };
            result.Joins.AddRange(
                standardFields
                    .Where((f) => f.Fields.Any() && IsSpecialColumn(f.Name) == true)
                    .Select(f => f.ToJoin(model, result))
                );
            if (parent == null)
                result.ConnectLinks(model);
            return result;
        }

        public TableJoin ToJoin(IDbModel model, GqlObjectQuery parent)
        {
            var onArg = Arguments.FirstOrDefault(a => a.Name == "on");

            if (onArg == null)
                throw new ExecutionError($"join on table {parent.GraphQlName} missing on argument.");

            var columns = (onArg.Value as IDictionary<string, object?>) ?? throw new ExecutionError($"While joining table {parent.GraphQlName}, unable to convert on value to object");
            if (columns.Keys.Count != 1)
                throw new ArgumentException("on joins only support two columns");
            var relation = columns.Values.First() as IDictionary<string, object?> ?? throw new ExecutionError($"While joining table {parent.GraphQlName}, unable to convert on value to a string");
            return new TableJoin
            {
                Name = Name,
                Alias = Alias,
                FromTable = parent,
                ConnectedTable = ToSqlData(model),
                FromColumn = columns.Keys.First(),
                ConnectedColumn = relation.Values?.First()?.ToString() ?? throw new ExecutionError($"While joining table {parent.GraphQlName}, unable to resolve join column {relation?.Keys?.FirstOrDefault()}"),
                Operator = relation.Keys.First(),
                JoinType = GetJoinType(Name),
            };
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

        private static string[] _specialColumns = new[] { "_join_", "_single_", "_agg_" };
        private static readonly (string, JoinType)[] ColumnTypeMap = new[]
        {
            ("_join_", JoinType.Join),
            ("_single_", JoinType.Single),
            ("_agg_", JoinType.Aggregate),
        };

        private static string NormalizeColumnName(string name)
        {
            var result = name;
            foreach (var (prefix,_) in ColumnTypeMap)
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

        private static JoinType GetJoinType(string name)
        {
            foreach (var (prefix, type) in ColumnTypeMap)
            {
                if (name.StartsWith(prefix))
                    return type;
            }
            return JoinType.Single;
        }
    }

}
