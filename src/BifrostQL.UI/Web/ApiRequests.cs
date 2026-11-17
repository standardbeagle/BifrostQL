namespace BifrostQL.UI.Web
{
    // Record types for the BifrostQL UI HTTP API requests.
    // ConnectionSetRequest was removed with /api/connection/set — see task XGSUbdBiIzla.

    // ConnectionTestRequest was removed with POST /api/connection/test: its only
    // caller was the SQLite form, which now uses /api/sqlite/test and
    // /api/sqlite/connect (see SqliteEndpoints).
    /// <summary>A SQLite file: <c>FilePath</c> (form) or <c>ConnectionString</c> (raw / recent), never both.</summary>
    public record SqliteConnectRequest(string? FilePath = null, string? ConnectionString = null, bool CreateNew = false);
    public record ListDatabasesRequest(string ConnectionString, string? Provider = null, bool PeerAuth = false, string? PsqlUser = null);
    // SshConnectRequest / WpDiscoverRequest were removed with the POST /api/ssh/connect
    // and POST /api/ssh/wp-discover HTTP routes: tunnel start + WordPress credential
    // discovery are host-internal (driven by /api/vault/connect), never on the HTTP
    // surface. See SshEndpoints.
    public record VaultConnectRequest(string Name);
    public record QuickstartRequest(string Schema, string? DataSize = "sample");
}
