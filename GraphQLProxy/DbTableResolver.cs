using GraphQL.Resolvers;
using GraphQL;
using GraphQLParser.AST;
using GraphQLProxy.Model;
using System.Data.SqlClient;
using GraphQL.Types;
using GraphQL.Validation.Complexity;
using static GraphQLProxy.DbTableResolver;
using System.Drawing;
using GraphQL.DataLoader;
using System.Collections.Concurrent;

namespace GraphQLProxy
{
    public interface IDbTableResolver : IFieldResolver
    {

    }

    public class DbTableResolver : IDbTableResolver
    {
        public DbTableResolver()
        {
        }
        public ValueTask<object?> ResolveAsync(IResolveFieldContext context)
        {
            var factory = context.RequestServices!.GetRequiredService<ITableReaderFactory>();
            return factory.ResolveAsync(context);
        }

        public sealed class TableFilter
        {
            public string? TableName { get; set; }
            public string ColumnName { get; set; } = null!;
            public string RelationName { get; set; } = null!;
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

                var baseSql = wrap + ChildTable.GetFilterSql() + ChildTable.GetSortAndPaging();
                return baseSql;
            }

            public override string ToString()
            {
                return $"{JoinName}";
            }
        }

        public enum JoinType
        {
            Join = 0,
            Single = 1,
        }

        public sealed class FragmentSpread
        {
            public string FragmentName { get; init; } = null!;
            public TableSqlData? Table { get; set; }

        }

        public sealed class TableSqlData
        {
            public TableSqlData? Parent => ParentJoin?.ParentTable;
            public TableJoin? ParentJoin { get; set; }
            public string TableName { get; set; } = "";
            public string Alias { get; set; } = "";
            public string KeyName => $"{Alias}:{TableName}";
            public List<string> ColumnNames { get; set; } = new List<string>();
            public List<string> Sort { get; set; } = new List<string>();
            public List<FragmentSpread> FragmentSpreads { get; set; } = new List<FragmentSpread>();
            public TableFilter? Filter { get; set; }
            public int? Limit { get; set; }
            public int? Offset { get; set; }
            public bool IsFragment { get; set; }

            public List<TableJoin> Joins { get; set; } = new List<TableJoin>();
            private IEnumerable<TableJoin> RecurseJoins => Joins.Concat(Joins.SelectMany(j => j.ChildTable.RecurseJoins));

            public IEnumerable<string> AllJoinNames => new[] { TableName }
            .Concat(Joins.SelectMany(j => j.ChildTable.AllJoinNames.Select(n => $"{j.JoinName}+{n}")));

            public IEnumerable<(string name, string alias)> FullColumnNames => 
                ColumnNames.Select(c => (c, c))
                .Concat(Joins.Select(j => (j.ParentColumn, j.ParentColumn)))
                .Distinct();

            public string GetFilterSql(string? alias = null)
            {
                if (Filter == null) return "";
                return " WHERE " + Filter.ToSql(alias);
            }

            public TableJoin GetJoin(string? alias, string name)
            {
                return RecurseJoins.First(j => j.Alias == alias && j.Name == name);
            }

            public Dictionary<string, string> ToSql()
            {
                var columnSql = String.Join(",", FullColumnNames.Select(n => $"[{n.name}] [{n.alias}]"));
                var cmdText = $"SELECT {columnSql} FROM [{TableName}]";

                var baseSql = cmdText + GetFilterSql() + GetSortAndPaging();
                var result = new Dictionary<string, string>();
                result.Add(KeyName, baseSql);
                foreach (var join in RecurseJoins)
                {
                    result.Add(join.JoinName, join.GetSql());
                }
                return result;
            }

            public string GetSortAndPaging()
            {
                var orderby = " ORDER BY (SELECT NULL)";
                if (Sort.Any())
                {
                    orderby = " ORDER BY " + String.Join(", ", Sort);
                }
                orderby += Offset != null ? $" OFFSET {Offset} ROWS" : " OFFSET 0 ROWS";
                orderby += Limit != null ? $" FETCH NEXT {Limit} ROWS ONLY" : "";
                return orderby;
            }

            public override string ToString()
            {
                return $"{TableName}";
            }

            public Action<object?>? GetArgumentSetter(string argumentName)
            {
                switch (argumentName)
                {
                    case "filter":
                        return value =>
                        {
                            var columnRow = (value as Dictionary<string, object?>)?.FirstOrDefault();
                            if (columnRow == null) return;
                            var operationRow = (columnRow?.Value as Dictionary<string, object?>)?.FirstOrDefault();
                            if (operationRow == null) return;
                            Filter = new TableFilter
                            {
                                ColumnName = columnRow?.Key!,
                                RelationName = operationRow?.Key!,
                                Value = operationRow?.Value
                            };

                        };
                    case "sort":
                        return value => Sort.AddRange((value as List<object?>)?.Cast<string>() ?? Array.Empty<string>());
                    case "limit":
                        return value => Limit = value as int?;
                    case "offset":
                        return value => Offset = value as int?;
                    case "on":
                        return value =>
                        {
                            var columns = (value as List<object?>)?.Cast<string>()?.ToArray() ?? Array.Empty<string>();
                            if (columns.Length != 2)
                                throw new ArgumentException("on joins only support two columns");
                            if (ParentJoin == null)
                                throw new ArgumentException("Parent Join cannot be null for 'on' argument");
                            ParentJoin.ParentColumn = columns[0];
                            ParentJoin.ChildColumn = columns[1];
                        };
                    default:
                        return value => { };
                }

            }
        }
    }


    public interface ITableReaderFactory
    {
        public ValueTask<object?> ResolveAsync(IResolveFieldContext context);
    }
    public sealed class TableReaderFactory : ITableReaderFactory
    {
        private List<TableSqlData>? _tables = null;
        public async ValueTask<object?> ResolveAsync(IResolveFieldContext context)
        {
            if (context.SubFields == null)
                throw new ArgumentNullException(nameof(context) + ".SubFields");

            _tables ??= await GetTables(context);

            return new ReaderEnum(
                _tables.First(t => t.Alias == (context.FieldAst.Alias?.Name.StringValue ?? "") && t.TableName == context.FieldAst.Name.StringValue),
                context.RequestServices!.GetRequiredService<IDbConnFactory>()
                );

        }

        private static async Task<List<TableSqlData>> GetTables(IResolveFieldContext context)
        {
            var visitor = new SqlVisitor();
            var sqlContext = new SqlContext() { Variables = context.Variables };
            await visitor.VisitAsync(context.Document, sqlContext);

            var newTables = sqlContext.GetFinalTables();
            return newTables;
        }
    }
}
