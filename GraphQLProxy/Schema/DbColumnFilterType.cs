using GraphQL.Types;
using GraphQLProxy.Model;
using System.Xml.Linq;

namespace GraphQLProxy.Schema
{
    public class DbColumnFilterType : InputObjectGraphType
    {
        public DbColumnFilterType(string table, IEnumerable<ColumnDto> columns)
        {
            Name = $"{table}ColumnFilterType";
            foreach (ColumnDto column in columns)
            {
                AddField(new FieldType
                {
                    Name = column.ColumnName,
                    ResolvedType = new DbFilterType(column.DataType),
                });
            }
        }
    }
}