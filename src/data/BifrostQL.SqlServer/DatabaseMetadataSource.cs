using BifrostQL.Core.Model;
using Microsoft.Data.SqlClient;

namespace BifrostQL.SqlServer
{
    /// <summary>
    /// Loads table/column metadata from a SQL Server configuration table (_bifrost_config).
    /// Expected schema: table_name (nvarchar), key (nvarchar), value (nvarchar).
    /// </summary>
    public sealed class DatabaseMetadataSource : IMetadataSource
    {
        private readonly string _connectionString;
        private readonly string? _configTableSchema;
        private readonly string _configTableName;

        public int Priority => 100;

        public DatabaseMetadataSource(string connectionString, string configTableName = "_bifrost_config")
        {
            ArgumentNullException.ThrowIfNull(connectionString, nameof(connectionString));
            ArgumentNullException.ThrowIfNull(configTableName, nameof(configTableName));
            var (schema, table) = SplitConfigTableName(configTableName);
            _connectionString = connectionString;
            _configTableSchema = schema;
            _configTableName = table;
        }

        public async Task<IDictionary<string, IDictionary<string, object?>>> LoadTableMetadataAsync()
        {
            var result = new Dictionary<string, IDictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);

            if (!await ConfigTableExistsAsync())
                return result;

            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            var sql = $"SELECT [table_name], [key], [value] FROM {QuoteSqlServerTableReference(_configTableSchema, _configTableName)}";
            var cmd = new SqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var tableName = reader.GetString(0);
                var key = reader.GetString(1);
                var value = reader.IsDBNull(2) ? null : reader.GetString(2);

                if (!result.TryGetValue(tableName, out var tableMetadata))
                {
                    tableMetadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    result[tableName] = tableMetadata;
                }
                tableMetadata[key] = value;
            }

            return result;
        }

        private async Task<bool> ConfigTableExistsAsync()
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            var sql = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = @tableName";
            if (!string.IsNullOrWhiteSpace(_configTableSchema))
                sql += " AND TABLE_SCHEMA = @tableSchema";

            var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@tableName", _configTableName);
            if (!string.IsNullOrWhiteSpace(_configTableSchema))
                cmd.Parameters.AddWithValue("@tableSchema", _configTableSchema);

            var count = (int)(await cmd.ExecuteScalarAsync())!;
            return count > 0;
        }

        internal static (string? Schema, string Table) SplitConfigTableName(string configTableName)
        {
            if (string.IsNullOrWhiteSpace(configTableName))
                throw new ArgumentException("Configuration table name cannot be empty.", nameof(configTableName));

            var trimmed = configTableName.Trim();
            var dotIndex = trimmed.LastIndexOf('.');
            if (dotIndex < 0)
                return (null, trimmed);

            var schema = trimmed[..dotIndex].Trim();
            var table = trimmed[(dotIndex + 1)..].Trim();
            if (schema.Length == 0 || table.Length == 0)
                throw new ArgumentException("Configuration table name must include both schema and table when qualified.", nameof(configTableName));

            return (schema, table);
        }

        internal static string QuoteSqlServerTableReference(string? schema, string table)
        {
            static string Quote(string identifier) => $"[{identifier.Replace("]", "]]")}]";

            return string.IsNullOrWhiteSpace(schema)
                ? Quote(table)
                : $"{Quote(schema)}.{Quote(table)}";
        }
    }
}
