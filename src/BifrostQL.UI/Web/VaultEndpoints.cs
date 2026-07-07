using System.Text;
using BifrostQL.Core.Model;
using BifrostQL.Core.Utils;
using BifrostQL.UI.Vault;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.UI.Web
{
    /// <summary>
    /// Vault endpoints: list saved servers (metadata only, never passwords) and
    /// connect using a saved server by name. Connecting keeps credentials
    /// server-side, starts an SSH tunnel when configured, optionally auto-discovers
    /// WordPress credentials, then activates the connection via
    /// <see cref="ConnectionState"/> + <see cref="ProfileActivation"/>.
    /// </summary>
    public static class VaultEndpoints
    {
        public static void MapVaultEndpoints(this WebApplication app, ConnectionState state, SshTunnelManager sshTunnel)
        {
            // GET /api/vault/servers — List saved servers (metadata only, no passwords)
            app.MapGet("/api/vault/servers", async () =>
            {
                try
                {
                    var servers = await VaultServerProvider.LoadServers(state.VaultPath);
                    var result = servers.Select(s => new
                    {
                        name = s.Server.Name,
                        provider = s.Server.Provider,
                        host = s.Server.Host,
                        port = s.Server.Port,
                        database = s.Server.Database,
                        tags = s.Server.Tags,
                        hasSsh = s.Server.Ssh is not null,
                        hasPassword = !string.IsNullOrEmpty(s.Server.Password),
                        source = s.Source,
                    });
                    return Results.Ok(result);
                }
                catch (Exception ex)
                {
                    // Do not mask a corrupt/tampered vault as an empty list — the UI
                    // must be able to tell "no servers configured" from "vault broken".
                    return Results.Problem(
                        title: "Vault could not be read",
                        detail: ex.Message,
                        statusCode: StatusCodes.Status500InternalServerError);
                }
            });

            // POST /api/vault/connect — Connect using a vault server by name (credentials stay server-side)
            app.MapPost("/api/vault/connect", async (VaultConnectRequest request, CancellationToken ct) =>
            {
                try
                {
                    var servers = await VaultServerProvider.LoadServers(state.VaultPath);
                    var match = servers.FirstOrDefault(s => s.Server.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase));
                    if (match.Server is null)
                        return Results.NotFound(new { success = false, error = $"Server '{request.Name}' not found" });

                    var server = match.Server;
                    var connStr = VaultServerProvider.BuildConnectionString(server);

                    // If SSH config present, start tunnel and rewrite connection string
                    if (server.Ssh is not null)
                    {
                        var remoteHost = server.Host;
                        var remotePort = server.Port;
                        var sshConfig = new SshTunnelConfig(
                            server.Ssh.Host, server.Ssh.Port, server.Ssh.Username,
                            server.Ssh.IdentityFile, remoteHost, remotePort);
                        var localPort = await sshTunnel.StartAsync(sshConfig, ct);

                        // WordPress credential auto-discovery runs when the vault entry is
                        // tagged "wordpress" AND has no explicit username. We `wp config get`
                        // over the SSH tunnel to populate DB_USER/DB_PASSWORD/DB_NAME. Other
                        // SSH-tunneled entries (no wordpress tag, or with explicit credentials)
                        // pass straight through and let the DB driver surface auth failures.
                        string? dbUser = null, dbPassword = null, dbName = null;
                        var wantsWpDiscovery =
                            server.Tags.Any(t => string.Equals(t, "wordpress", StringComparison.OrdinalIgnoreCase))
                            && string.IsNullOrWhiteSpace(server.Username);
                        if (wantsWpDiscovery)
                        {
                            WpCredentials? discovered = null;
                            Exception? lastError = null;

                            foreach (var wpRoot in WordPressDiscovery.DefaultRoots)
                            {
                                try
                                {
                                    var wpConfig = new WpDiscoverConfig("wp", wpRoot);
                                    var creds = await sshTunnel.DiscoverWordPressAsync(sshConfig, wpConfig, ct);
                                    if (!string.IsNullOrWhiteSpace(creds.DbUser))
                                    {
                                        discovered = creds;
                                        break;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    lastError = ex;
                                }
                            }

                            if (discovered is null)
                            {
                                var rootList = string.Join(", ", WordPressDiscovery.DefaultRoots);
                                var detail = lastError?.Message ?? "no installations found";
                                return Results.BadRequest(new
                                {
                                    success = false,
                                    error = $"WordPress auto-discovery failed (searched {rootList}): {detail}. " +
                                            "Set explicit Username/Password on the vault entry, or drop the " +
                                            "'wordpress' tag to skip auto-discovery."
                                });
                            }

                            dbUser = discovered.DbUser;
                            dbPassword = discovered.DbPassword;
                            dbName = discovered.DbName;
                        }

                        // Rewrite to tunnel through localhost with discovered credentials
                        var tunneled = server with
                        {
                            Host = "127.0.0.1",
                            Port = localPort,
                            Username = dbUser ?? server.Username,
                            Password = dbPassword ?? server.Password,
                            Database = dbName ?? server.Database
                        };
                        connStr = VaultServerProvider.BuildConnectionString(tunneled);
                    }

                    var provider = DbConnFactoryResolver.ParseProviderName(server.Provider);
                    state.ConnectionString = connStr;
                    state.Provider = provider;

                    state.Options?.BindConnectionString(connStr);
                    state.Options?.BindProvider(provider.ToString().ToLowerInvariant());
                    // Arbitrary vault DB — no bundled profile config, so only the synthesized
                    // raw default is offered. Clear any profiles left over from a prior connect.
                    await ProfileActivation.RebindProfilesAsync(app.Services, null);
                    state.Options?.ResetSchema(app.Services);

                    return Results.Ok(new
                    {
                        success = true,
                        provider = server.Provider,
                        server = server.Host,
                        database = server.Database ?? "",
                        name = server.Name,
                    });
                }
                catch (Exception ex)
                {
                    // SECURITY: this is a desktop UI — stack traces and inner-exception
                    // dumps have no business crossing the HTTP boundary to the renderer,
                    // and shipping them there rests the entire secret-safety guarantee on
                    // SecretScrubber's regex coverage (DB drivers embed the full connection
                    // string, Password= and all, inside exception messages / Data dicts /
                    // stack frames). So the HTTP response carries ONLY a scrubbed,
                    // user-actionable message and a correlation id; the full detail
                    // (still scrubbed, belt-and-suspenders) is logged server-side and
                    // referenced by that id.
                    var logger = app.Services.GetRequiredService<ILogger<Program>>();

                    var correlationId = Guid.NewGuid().ToString("N")[..8];
                    var scrubbedMessage = SecretScrubber.Scrub(ex.Message) ?? "";

                    var scrubbedDetailsBuilder = new StringBuilder();
                    scrubbedDetailsBuilder.Append(scrubbedMessage);
                    scrubbedDetailsBuilder.Append("\n\nStack trace:\n");
                    scrubbedDetailsBuilder.Append(SecretScrubber.Scrub(ex.StackTrace) ?? "");
                    if (ex.InnerException != null)
                    {
                        scrubbedDetailsBuilder.Append("\n\nInner exception: ");
                        scrubbedDetailsBuilder.Append(SecretScrubber.Scrub(ex.InnerException.Message) ?? "");
                        scrubbedDetailsBuilder.Append('\n');
                        scrubbedDetailsBuilder.Append(SecretScrubber.Scrub(ex.InnerException.StackTrace) ?? "");
                    }

                    // Do NOT pass `ex` directly to the logger — the default logging
                    // formatters call ex.ToString() which would bypass the scrubber.
                    // Instead log the exception type + scrubbed detail as positional args.
                    logger.LogError(
                        "Vault connect failed for '{ServerName}' [{CorrelationId}] ({ExceptionType}): {ScrubbedDetails}",
                        request.Name,
                        correlationId,
                        ex.GetType().FullName,
                        scrubbedDetailsBuilder.ToString());

                    return Results.BadRequest(new
                    {
                        success = false,
                        error = scrubbedMessage,
                        correlationId
                    });
                }
            });
        }
    }
}
