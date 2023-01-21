using GraphQL.Types;
using BifrostQL.Model;
using System.Xml.Linq;

namespace BifrostQL.Schema
{
    public class DbColumnFilterType : InputObjectGraphType
    {
        public DbColumnFilterType(TableDto table)
        {
            Name = $"{table.GraphQLName}ColumnFilterType";
            foreach (ColumnDto column in table.Columns)
            {
                AddField(new FieldType
                {
                    Name = column.ColumnName,
                    ResolvedType = new DbFilterType(column.DataType),
                });
            }
            foreach(var link in table.SingleLinks)
            {
                AddField(new FieldType
                {
                    Name = link.Key,
                    ResolvedType = new GraphQLTypeReference($"{link.Value.ParentTable.GraphQLName}ColumnFilterType"),
                });
            }
        }
    }
}