using GraphQL.Types;
using GraphQLProxy.Model;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace GraphQLProxy.Schema
{
    public class DbRow : ObjectGraphType
    {
        private readonly TableDto _table;
        public DbRow(TableDto table)
        {
            Name = table.GraphQLName;
            _table = table;
            foreach (var column in table.Columns)
            {
                switch (column.DataType)
                {
                    case "int":
                    case "smallint":
                    case "tinyint":
                        Field<int>(column.ColumnName, nullable: column.IsNullable).Resolve(DbJoinFieldResolver.Instance);
                        break;
                    case "decimal":
                        Field<decimal>(column.ColumnName, nullable: column.IsNullable).Resolve(DbJoinFieldResolver.Instance);
                        break;
                    case "bigint":
                        Field<BigInteger>(column.ColumnName, nullable: column.IsNullable).Resolve(DbJoinFieldResolver.Instance);
                        break;
                    case "float":
                    case "real":
                        Field<double>(column.ColumnName, nullable: column.IsNullable).Resolve(DbJoinFieldResolver.Instance);
                        break;
                    case "datetime":
                        Field<DateTime>(column.ColumnName, nullable: column.IsNullable).Resolve(DbJoinFieldResolver.Instance);
                        break;
                    case "datetime2":
                        Field<DateTime>(column.ColumnName, nullable: column.IsNullable).Resolve(DbJoinFieldResolver.Instance);
                        break;
                    case "datetimeoffset":
                        Field<DateTimeOffset>(column.ColumnName, nullable: column.IsNullable).Resolve(DbJoinFieldResolver.Instance);
                        break;
                    case "bit":
                        Field<bool>(column.ColumnName, nullable: column.IsNullable).Resolve(DbJoinFieldResolver.Instance);
                        break;
                    case "varchar":
                    case "nvarchar":
                    case "char":
                    case "nchar":
                    case "binary":
                    case "varbinary":
                    case "text":
                    case "ntext":
                    default:
                        Field<string>(column.ColumnName, nullable: column.IsNullable).Resolve(DbJoinFieldResolver.Instance);
                        break;
                }
            }
        }

        public void AddLinks(IDictionary<string, DbRow> rows)
        {
            foreach (var multi in _table.MultiLinks)
            {
                AddField(new FieldType
                {
                    Name = $"{multi.Name.Replace(" ", "__")}",
                    Arguments = new QueryArguments(
                        new QueryArgument(new GraphQLTypeReference($"{multi.ChildTable.GraphQLName}ColumnFilterType")) { Name = "filter" },
                        new QueryArgument<ListGraphType<StringGraphType>>() { Name = "sort" }),
                    ResolvedType = new ListGraphType(rows[multi.ChildTable.UniqueName]),
                    Resolver = DbJoinFieldResolver.Instance
                });
            }
            foreach (var single in _table.SingleLinks)
            {
                AddField(new FieldType
                {
                    Name = $"{single.Name.Replace(" ", "__")}",
                    ResolvedType = rows[single.ParentTable.UniqueName],
                    Resolver = DbJoinFieldResolver.Instance
                });
            }
        }
        public void AddTableJoin(TableDto table, DbRow type)
        {
            AddField(new FieldType
            {
                Name = $"_join_{table.GraphQLName}",
                Arguments = new QueryArguments(
                    new QueryArgument<ListGraphType<StringGraphType>>() { Name = "on" },
                    //new QueryArgument<StringGraphType>() { Name = "fk" },
                    new QueryArgument(new GraphQLTypeReference($"{table.GraphQLName}ColumnFilterType")) { Name = "filter" },
                    new QueryArgument<ListGraphType<StringGraphType>>() { Name = "sort" }),
                ResolvedType = new ListGraphType(type),
                Resolver = DbJoinFieldResolver.Instance
            });
            //AddField(new FieldType
            //{
            //    Name = $"_single_{table.GraphQLName}",
            //    Arguments = new QueryArguments(new QueryArgument<ListGraphType<StringGraphType>>() { Name = "on" }),
            //    ResolvedType = type,
            //    Resolver = DbJoinFieldResolver.Instance
            //});
        }
    }

}
