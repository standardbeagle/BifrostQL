using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BifrostQL.Core.Model
{
    public interface IMetadataLoader
    {
        void ApplyDatabaseMetadata(IDictionary<string, object?> metadata, string rootName = ":root");
        void ApplySchemaMetadata(IDbSchema schema, IDictionary<string, object?> metadata);
        void ApplyTableMetadata(IDbTable table, IDictionary<string, object?> metadata);
        void ApplyColumnMetadata(IDbTable table, ColumnDto column, IDictionary<string, object?> metadata);
    }
}
