using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace BifrostQL.Core.Model
{
    public class MetadataLoader : IMetadataLoader
    {
        private readonly IConfiguration _configuration;
        private readonly string _key;

        public MetadataLoader(IConfiguration configuration, string key)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _key = key ?? throw new ArgumentNullException(nameof(key));
        }

        public IDictionary<string, object?> GetDatabaseMetadata()
        {
            throw new NotImplementedException();
            //return _configuration[_key] ?? throw new InvalidOperationException($"Could not find configuration key: {_key}");
        }

        public IDictionary<string, object?> GetTableMetadata(IDbTable table)
        {
            throw new NotImplementedException();
        }

        public IDictionary<string, object?> GetColumnMetadata(ISchemaNames column)
        {
            throw new NotImplementedException();
        }
    }
}
