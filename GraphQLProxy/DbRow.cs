using GraphQL.Types;
using GraphQLProxy.Model;
using System.Numerics;
using System.Xml.Linq;

namespace GraphQLProxy
{
    public class DbRow : ObjectGraphType
    {
        public DbRow(TableDto table)
        {
            Name = table.TableName.Replace(" ", "__");
            foreach (var column in table.Columns)
            {
                switch (column.DataType)
                {
                    case "int":
                    case "smallint":
                    case "tinyint":
                        Field<int>(column.ColumnName).Resolve(DbJoinFieldResolver.Instance);
                        break;
                    case "decimal":
                        Field<decimal>(column.ColumnName).Resolve(DbJoinFieldResolver.Instance);
                        break;
                    case "bigint":
                        Field<BigInteger>(column.ColumnName).Resolve(DbJoinFieldResolver.Instance);
                        break;
                    case "float":
                    case "real":
                        Field<double>(column.ColumnName).Resolve(DbJoinFieldResolver.Instance);
                        break;
                    case "datetime":
                        Field<DateTime>(column.ColumnName).Resolve(DbJoinFieldResolver.Instance);
                        break;
                    case "datetime2":
                        Field<DateTime>(column.ColumnName).Resolve(DbJoinFieldResolver.Instance);
                        break;
                    case "datetimeoffset":
                        Field<DateTimeOffset>(column.ColumnName).Resolve(DbJoinFieldResolver.Instance);
                        break;
                    case "bit":
                        Field<bool>(column.ColumnName).Resolve(DbJoinFieldResolver.Instance);
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
                        Field<string>(column.ColumnName).Resolve(DbJoinFieldResolver.Instance);
                        break;
                }
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
