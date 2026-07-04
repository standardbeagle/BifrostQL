using BifrostQL.Core.AppMetadata;
using Microsoft.Data.SqlClient;

namespace BifrostQL.SqlServer
{
    /// <summary>
    /// Loads the app-metadata overlay from a SQL Server table (default
    /// <c>_bifrost_app_metadata</c>). Expected schema: <c>table_name</c>
    /// (nvarchar) holding the qualified table name, <c>metadata</c> (nvarchar)
    /// holding the <see cref="EntityMetadata"/> for that entity serialized with
    /// the stable camelCase contract. A missing table yields an empty overlay.
    /// </summary>
    public sealed class DatabaseAppMetadataSource : IAppMetadataSource
    {
        private readonly string _connectionString;
        private readonly string _tableName;

        public int Priority { get; }

        public DatabaseAppMetadataSource(
            string connectionString,
            string tableName = "_bifrost_app_metadata",
            int priority = 100)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
            ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
            _connectionString = connectionString;
            _tableName = tableName.Replace("]", "]]");
            Priority = priority;
        }

        public async Task<IDictionary<string, EntityMetadata>> LoadEntityMetadataAsync()
        {
            var result = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase);

            if (!await TableExistsAsync())
                return result;

            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            var sql = $"SELECT [table_name], [metadata] FROM [{_tableName}]";
            var cmd = new SqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var tableName = reader.GetString(0);
                if (reader.IsDBNull(1))
                    continue;

                var entityJson = reader.GetString(1);
                var entity = AppMetadataJson.DeserializeEntity(entityJson);
                result[tableName] = entity;
            }

            return result;
        }

        private async Task<bool> TableExistsAsync()
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            var sql = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = @tableName";
            var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@tableName", _tableName);
            var count = (int)(await cmd.ExecuteScalarAsync())!;
            return count > 0;
        }
    }
}
