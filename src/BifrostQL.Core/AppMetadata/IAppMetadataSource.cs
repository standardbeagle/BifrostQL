using Microsoft.Data.SqlClient;

namespace BifrostQL.Core.AppMetadata
{
    /// <summary>
    /// Provides app-metadata overlay entries from an external source (a JSON
    /// file, a database table, etc.). Each source returns entity-overlay
    /// metadata keyed by qualified table name (e.g. <c>dbo.users</c>) so the
    /// overlay aligns with <c>DbModel</c> tables without modifying them.
    ///
    /// This mirrors the shape of <c>BifrostQL.Core.Model.IMetadataSource</c>,
    /// but it is a separate, coexisting pipeline: the app-metadata overlay is
    /// loaded and exposed alongside — never merged into — the schema-metadata
    /// system.
    /// </summary>
    public interface IAppMetadataSource
    {
        /// <summary>
        /// Loads app-metadata overlay entries from this source. Keys are
        /// qualified table names; values are the <see cref="EntityMetadata"/>
        /// overlay for each entity.
        /// </summary>
        Task<IDictionary<string, EntityMetadata>> LoadEntityMetadataAsync();

        /// <summary>
        /// Priority of this source. Higher values override lower values when
        /// multiple sources contribute an entry for the same qualified table
        /// name. File-based sources should use low values (e.g. 0), database
        /// sources higher (e.g. 100).
        /// </summary>
        int Priority { get; }
    }

    /// <summary>
    /// Loads the app-metadata overlay from a JSON file on disk. The file
    /// content is the stable camelCase contract defined by
    /// <see cref="AppMetadataJson"/>. A missing file yields an empty overlay so
    /// the source is inert when no overlay file is deployed.
    /// </summary>
    public sealed class FileAppMetadataSource : IAppMetadataSource
    {
        private readonly string _filePath;

        public int Priority { get; }

        public FileAppMetadataSource(string filePath, int priority = 0)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            _filePath = filePath;
            Priority = priority;
        }

        public async Task<IDictionary<string, EntityMetadata>> LoadEntityMetadataAsync()
        {
            if (!File.Exists(_filePath))
                return new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase);

            var json = await File.ReadAllTextAsync(_filePath);
            var model = AppMetadataJson.Deserialize(json);
            return new Dictionary<string, EntityMetadata>(model.Entities, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Loads the app-metadata overlay from a database table (default
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

    /// <summary>
    /// Merges app-metadata overlay entries from multiple sources in priority
    /// order. When more than one source contributes an entry for the same
    /// qualified table name, the higher-priority source's entry wins; entries
    /// are not deep-merged because each source supplies a complete
    /// <see cref="EntityMetadata"/> for an entity.
    /// </summary>
    public sealed class CompositeAppMetadataSource : IAppMetadataSource
    {
        private readonly IReadOnlyList<IAppMetadataSource> _sources;

        public int Priority => _sources.Count > 0 ? _sources.Max(s => s.Priority) : 0;

        public CompositeAppMetadataSource(IReadOnlyList<IAppMetadataSource> sources)
        {
            ArgumentNullException.ThrowIfNull(sources);
            _sources = sources.OrderBy(s => s.Priority).ToList();
        }

        public async Task<IDictionary<string, EntityMetadata>> LoadEntityMetadataAsync()
        {
            var merged = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase);

            foreach (var source in _sources)
            {
                var sourceData = await source.LoadEntityMetadataAsync();
                foreach (var (tableName, entity) in sourceData)
                {
                    merged[tableName] = entity;
                }
            }

            return merged;
        }
    }
}
