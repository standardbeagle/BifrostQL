using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BifrostQL.Core.Model
{
    public interface IMetadataLoader
    {
        IDictionary<string, object?> GetDatabaseMetadata();
        IDictionary<string, object?> GetTableMetadata(IDbTable table);
        IDictionary<string, object?> GetColumnMetadata(ISchemaNames column);
    }
}
