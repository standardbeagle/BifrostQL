using GraphQL.Types;
using GraphQLProxy.Model;
using System.Numerics;
using System.Xml.Linq;

namespace GraphQLProxy
{
    public class DbInputRow : InputObjectGraphType
    {
        public DbInputRow(TableDto table)
        {
            Name = "Input" + table.TableName.Replace(" ", "__");
            foreach (var column in table.Columns)
            {
                switch (column.DataType)
                {
                    case "int":
                    case "smallint":
                    case "tinyint":
                        Field<int>(column.ColumnName);
                        break;
                    case "decimal":
                        Field<decimal>(column.ColumnName);
                        break;
                    case "bigint":
                        Field<BigInteger>(column.ColumnName);
                        break;
                    case "float":
                    case "real":
                        Field<double>(column.ColumnName);
                        break;
                    case "datetime":
                        Field<DateTime>(column.ColumnName);
                        break;
                    case "datetime2":
                        Field<DateTime>(column.ColumnName);
                        break;
                    case "datetimeoffset":
                        Field<DateTimeOffset>(column.ColumnName);
                        break;
                    case "bit":
                        Field<bool>(column.ColumnName);
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
                        Field<string>(column.ColumnName);
                        break;
                }
            }
        }
    }
}
