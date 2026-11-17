namespace BifrostQL.Core.Model
{
    /// <summary>
    /// Tables Bifrost provisions for its own bookkeeping inside a served database. The local-auth
    /// state tables (sessions, lockouts, password history, the authentication audit trail) are never
    /// part of a served model, whatever the metadata says: <c>DbModel</c> files them under
    /// <see cref="IDbModel.HiddenTables"/>, so no GraphQL field or protocol adapter can reach them.
    /// </summary>
    public static class BifrostSystemTables
    {
        /// <summary>Name prefix of every local-auth state table (<c>DbLocalAuthStateStore</c>).</summary>
        public const string AuthStatePrefix = "_bifrost_auth_";

        /// <summary>True for a table that is hidden from every served model regardless of metadata.</summary>
        public static bool IsAlwaysHidden(string dbName) =>
            dbName.StartsWith(AuthStatePrefix, StringComparison.OrdinalIgnoreCase);
    }
}
