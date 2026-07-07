namespace BifrostQL.UI.Web
{
    // Record types for the BifrostQL UI HTTP API requests.
    // ConnectionSetRequest was removed with /api/connection/set — see task XGSUbdBiIzla.

    public record ConnectionTestRequest(string ConnectionString, string? Provider = null);
    public record ListDatabasesRequest(string ConnectionString, string? Provider = null, bool PeerAuth = false, string? PsqlUser = null);
    // SshConnectRequest / WpDiscoverRequest were removed with the POST /api/ssh/connect
    // and POST /api/ssh/wp-discover HTTP routes: tunnel start + WordPress credential
    // discovery are host-internal (driven by /api/vault/connect), never on the HTTP
    // surface. See SshEndpoints.
    public record VaultConnectRequest(string Name);
    public record QuickstartRequest(string Schema, string? DataSize = "sample");
}
