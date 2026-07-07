namespace BifrostQL.UI.Web
{
    /// <summary>
    /// SSH tunnel endpoints exposed on the HTTP surface: only disconnect and status.
    ///
    /// SECURITY: tunnel <b>start</b> and WordPress credential <b>discovery</b> are
    /// deliberately NOT on the HTTP surface. Both take attacker-influenceable input
    /// and (for wp-discover) return the WordPress DB password; on an unauthenticated
    /// loopback endpoint, chained with cross-origin reach, that exfiltrates
    /// credentials. Like RawSql and visual-query, those operations are host-internal:
    /// the only caller is <c>POST /api/vault/connect</c>, which starts the tunnel and
    /// discovers WordPress credentials in-process against a saved vault entry and
    /// never returns the password. The SPA only ever calls <c>/api/ssh/disconnect</c>.
    /// </summary>
    public static class SshEndpoints
    {
        public static void MapSshEndpoints(this WebApplication app, SshTunnelManager sshTunnel)
        {
            // POST /api/ssh/disconnect — Stop the SSH tunnel
            app.MapPost("/api/ssh/disconnect", async () =>
            {
                await sshTunnel.StopAsync();
                return Results.Ok(new { success = true });
            });

            // GET /api/ssh/status — Check tunnel status (no secrets in the payload)
            app.MapGet("/api/ssh/status", () => Results.Ok(sshTunnel.GetStatus()));
        }
    }
}
