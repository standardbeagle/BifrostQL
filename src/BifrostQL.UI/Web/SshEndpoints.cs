namespace BifrostQL.UI.Web
{
    /// <summary>
    /// SSH tunnel endpoints: start/stop/status and WordPress credential discovery
    /// over SSH via wp-cli. All operate against the shared <see cref="SshTunnelManager"/>.
    /// </summary>
    public static class SshEndpoints
    {
        public static void MapSshEndpoints(this WebApplication app, SshTunnelManager sshTunnel)
        {
            // POST /api/ssh/connect — Start an SSH tunnel
            app.MapPost("/api/ssh/connect", async (SshConnectRequest request, CancellationToken ct) =>
            {
                try
                {
                    var config = new SshTunnelConfig(
                        request.SshHost, request.SshPort, request.SshUsername,
                        request.IdentityFile, request.RemoteHost, request.RemotePort);
                    var localPort = await sshTunnel.StartAsync(config, ct);
                    return Results.Ok(new { success = true, localPort });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/ssh/disconnect — Stop the SSH tunnel
            app.MapPost("/api/ssh/disconnect", async () =>
            {
                await sshTunnel.StopAsync();
                return Results.Ok(new { success = true });
            });

            // GET /api/ssh/status — Check tunnel status
            app.MapGet("/api/ssh/status", () => Results.Ok(sshTunnel.GetStatus()));

            // POST /api/ssh/wp-discover — Discover WordPress DB credentials via wp-cli over SSH
            app.MapPost("/api/ssh/wp-discover", async (WpDiscoverRequest request, CancellationToken ct) =>
            {
                try
                {
                    var sshConfig = new SshTunnelConfig(
                        request.SshHost, request.SshPort, request.SshUsername,
                        request.IdentityFile, "localhost", 3306);
                    var wpConfig = new WpDiscoverConfig(request.WpPath, request.WpRoot);
                    var creds = await sshTunnel.DiscoverWordPressAsync(sshConfig, wpConfig, ct);
                    return Results.Ok(new
                    {
                        success = true,
                        dbName = creds.DbName,
                        dbUser = creds.DbUser,
                        dbPassword = creds.DbPassword,
                        dbHost = creds.DbHost,
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });
        }
    }
}
