using GraphQL.Types;
using BifrostQL.Model;
using System.Numerics;
using System.Xml.Linq;

namespace BifrostQL.Schema
{
    public enum IdentityType
    {
        None,
        Optional,
        Required
    }
    public class DbInputRow : InputObjectGraphType
    {
        public DbInputRow(string action, TableDto table, IdentityType identityType)
        {
            Name = action + "_" + table.TableName.Replace(" ", "__");
            foreach (var column in table.Columns)
            {
                if (identityType == IdentityType.None && column.IsIdentity)
                    continue;

                var isNullable = column.IsNullable;
                if (column.IsCreatedOnColumn || column.IsCreatedByColumn || column.IsUpdatedByColumn || column.IsUpdatedOnColumn)
                    isNullable = true;
                if (identityType == IdentityType.Optional && column.IsIdentity)
                    isNullable = true;
                if (identityType == IdentityType.Required && column.IsIdentity)
                    isNullable = false;

                switch (column.DataType)
                {
                    case "int":
                    case "smallint":
                    case "tinyint":
                        base.Field<int>(column.ColumnName, nullable: isNullable);
                        break;
                    case "decimal":
                        base.Field<decimal>(column.ColumnName, nullable: isNullable);
                        break;
                    case "bigint":
                        base.Field<BigInteger>(column.ColumnName, nullable: isNullable);
                        break;
                    case "float":
                    case "real":
                        base.Field<double>(column.ColumnName, nullable: isNullable);
                        break;
                    case "datetime":
                        base.Field<DateTime>(column.ColumnName, nullable: isNullable);
                        break;
                    case "datetime2":
                        base.Field<DateTime>(column.ColumnName, nullable: isNullable);
                        break;
                    case "datetimeoffset":
                        base.Field<DateTimeOffset>(column.ColumnName, nullable: isNullable);
                        break;
                    case "bit":
                        base.Field<bool>(column.ColumnName, nullable: isNullable);
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
                        base.Field<string>(column.ColumnName, nullable: isNullable);
                        break;
                }
            }
        }
    }
}
