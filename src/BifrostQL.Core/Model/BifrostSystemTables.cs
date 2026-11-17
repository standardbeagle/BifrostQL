namespace BifrostQL.Core.Model
{
    /// <summary>
    /// Tables Bifrost provisions for its own bookkeeping inside a served database. The local-auth
    /// state tables (sessions, lockouts, password history, the authentication audit trail), the
    /// saved-object table and the SQL Server metadata tables (schema metadata and app metadata) are
    /// never part of a served model, whatever the metadata says:
    /// <c>DbModel</c> files them under <see cref="IDbModel.HiddenTables"/>, so no GraphQL field or
    /// protocol adapter can reach them. Each family owns a name prefix, and a configurable table name
    /// must keep it, so hiding by name needs no wiring from the store to the model.
    /// </summary>
    public static class BifrostSystemTables
    {
        /// <summary>Name prefix of every local-auth state table (<c>DbLocalAuthStateStore</c>).</summary>
        public const string AuthStatePrefix = "_bifrost_auth_";

        /// <summary>
        /// Name prefix of the saved-object table (<c>DbSavedObjectStore</c>). It is also the default
        /// table name; a configured name must start with it.
        /// </summary>
        public const string SavedObjectsPrefix = "_bifrost_saved_objects";

        /// <summary>
        /// Name prefix of the schema-metadata table (<c>DatabaseMetadataSource</c>). Its rows set
        /// security metadata (tenant filters, soft delete, policies), so writing it through the API it
        /// configures would be privilege escalation. It is also the default name.
        /// </summary>
        public const string ConfigPrefix = "_bifrost_config";

        /// <summary>Name prefix of the app-metadata table (<c>DatabaseAppMetadataSource</c>); also its default name.</summary>
        public const string AppMetadataPrefix = "_bifrost_app_metadata";

        private static readonly string[] Prefixes = { AuthStatePrefix, SavedObjectsPrefix, ConfigPrefix, AppMetadataPrefix };

        /// <summary>True for a table that is hidden from every served model regardless of metadata.</summary>
        public static bool IsAlwaysHidden(string dbName)
        {
            foreach (var prefix in Prefixes)
            {
                if (dbName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Throws when a configured table name leaves its family's reserved prefix, which would put the
        /// table back in the served model.
        /// </summary>
        public static void RequirePrefix(string tableName, string prefix, string paramName)
        {
            if (!tableName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    $"The table name must start with '{prefix}' so it stays hidden from every served model.", paramName);
        }
    }
}
