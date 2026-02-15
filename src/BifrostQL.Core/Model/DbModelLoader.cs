using BifrostQL.Core.Model;
using Microsoft.Extensions.Configuration;
using Pluralize.NET.Core;
using System.Data;

namespace BifrostQL.Model
{
    public sealed class DbModelLoader
    {
        private readonly IDbConnFactory _connFactory;
        private readonly IMetadataLoader _metadataLoader;

        // Backward compatibility constructor
        public DbModelLoader(string connectionString, IMetadataLoader metadataLoader)
            : this(new DbConnFactory(connectionString), metadataLoader)
        {
        }

        public DbModelLoader(IDbConnFactory connFactory, IMetadataLoader metadataLoader)
        {
            _connFactory = connFactory;
            _metadataLoader = metadataLoader;
        }

        public Task<IDbModel> LoadAsync()
        {
            return LoadAsync(null);
        }

        public async Task<IDbModel> LoadAsync(IDictionary<string, IDictionary<string, object?>>? additionalMetadata)
        {
            await using var conn = _connFactory.GetConnection();
            await conn.OpenAsync();

            var schemaData = await _connFactory.SchemaReader.ReadSchemaAsync(conn);

            return DbModel.FromTables(
                schemaData.Tables,
                _metadataLoader,
                Array.Empty<DbStoredProcedure>(),
                Array.Empty<DbForeignKey>(),
                additionalMetadata);
        }
    }
}
