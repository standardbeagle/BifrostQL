namespace BifrostQL.UI.Web
{
    // Record types for the BifrostQL UI HTTP API requests.
    // ConnectionSetRequest was removed with /api/connection/set — see task XGSUbdBiIzla.

    public record ConnectionTestRequest(string ConnectionString, string? Provider = null);
    public record ListDatabasesRequest(string ConnectionString, string? Provider = null, bool PeerAuth = false, string? PsqlUser = null);
    public record SshConnectRequest(string SshHost, int SshPort, string SshUsername,
        string? IdentityFile, string RemoteHost, int RemotePort);
    public record WpDiscoverRequest(string SshHost, int SshPort, string SshUsername,
        string? IdentityFile, string? WpPath, string? WpRoot);
    public record VaultConnectRequest(string Name);
    public record QuickstartRequest(string Schema, string? DataSize = "sample");
}
