using BifrostQL.Core.Model;
using BifrostQL.Mcp;
using BifrostQL.Core.Modules;
using BifrostQL.MySql;
using BifrostQL.Ngsql;
using BifrostQL.Server;
using BifrostQL.Sqlite;
using BifrostQL.SqlServer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

// Register all dialect factories so DbConnFactoryResolver can route by
// provider. BifrostQL.Host is the reference server implementation, so it
// wires up every shipped dialect rather than requiring callers to add
// project references themselves.
DbConnFactoryResolver.Register(BifrostDbProvider.SqlServer, cs => new SqlServerDbConnFactory(cs));
DbConnFactoryResolver.Register(BifrostDbProvider.PostgreSql, cs => new PostgresDbConnFactory(cs));
DbConnFactoryResolver.Register(BifrostDbProvider.MySql, cs => new MySqlDbConnFactory(cs));
DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));

var builder = WebApplication.CreateBuilder(args);

const string bifrostSmartScheme = "BifrostSmartAuth";
var jwtConfig = builder.Configuration.GetSection("JwtSettings");
var authEnabled = !builder.Configuration.GetValue("BifrostQL:DisableAuth", false);

// Required for microsoft ad b2c tokens
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.MaxRequestHeadersTotalSize = 131072;
});

// HTTP/3 support (QUIC transport with automatic HTTP/2 and HTTP/1.1 fallback).
// Enable via configuration: BifrostQL:Http3:Enabled = true
var http3Config = builder.Configuration.GetSection("BifrostQL:Http3");
if (http3Config.GetValue("Enabled", false))
{
    builder.UseBifrostHttp3(opts =>
    {
        if (http3Config.GetValue<int?>("HttpsPort") is int httpsPort)
            opts.HttpsPort = httpsPort;
        if (http3Config.GetValue<int?>("HttpPort") is int httpPort)
            opts.HttpPort = httpPort;
    });
}

builder.Services.AddBifrostQL(options =>
{
    options
        .BindStandardConfig(builder.Configuration);
});

// Bearer/API clients must authenticate against the JWT scheme (and get a 401 on failure),
// while the interactive UI keeps cookie + OIDC login. AddBifrostQL already registered the
// cookie and OIDC handlers and set the default scheme to cookie; this runs afterwards so its
// per-request selector wins for authenticate/challenge. A request carrying an
// `Authorization: Bearer` header is forwarded to the JWT scheme; everything else falls
// through to cookie, leaving the browser login flow unchanged. Sign-in stays on cookie so
// interactive login still issues a session cookie. (Previously a second AddAuthentication in
// AddBifrostQL clobbered the JWT default, so Bearer clients got an OIDC 302 instead of a 401,
// and UseAuthentication ran twice.)
if (jwtConfig.Exists() && authEnabled)
{
    builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = bifrostSmartScheme;
        options.DefaultAuthenticateScheme = bifrostSmartScheme;
        options.DefaultChallengeScheme = bifrostSmartScheme;
        options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme,
        options => builder.Configuration.Bind("JwtSettings", options))
    .AddPolicyScheme(bifrostSmartScheme, "Bifrost Bearer-or-Cookie selector", options =>
    {
        options.ForwardDefaultSelector = context =>
        {
            var authorization = context.Request.Headers.Authorization.ToString();
            return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? JwtBearerDefaults.AuthenticationScheme
                : CookieAuthenticationDefaults.AuthenticationScheme;
        };
    });
}

builder.Services.AddCors();

// MCP over Streamable HTTP (opt-in): BifrostQL:Mcp:Http:Enabled = true. The front
// door carries the host's own auth posture: with auth on it runs in bearer mode and
// the endpoint below requires an authenticated caller, so an anonymous request never
// reaches the MCP session at all. Writes stay off.
var mcpHttpEnabled = builder.Configuration.GetValue("BifrostQL:Mcp:Http:Enabled", false);
IServiceProvider? hostServices = null;
if (mcpHttpEnabled)
{
    var mcpAuth = new McpAuthOptions();
    if (authEnabled)
    {
        mcpAuth.Mode = McpAuthMode.Bearer;
        // The endpoint is authorized against the JWT scheme, so ASP.NET's own handler has
        // already validated the token this delegate is handed; the authenticated principal
        // of the current request IS the validated identity. Standing up a second token
        // handler here would give the host two validation configurations that can disagree
        // — the MCP surface must not be reachable under laxer rules than /graphql. An
        // unauthenticated request yields null, which the adapter refuses.
        mcpAuth.ValidateBearerToken = _ =>
        {
            var user = hostServices?.GetService<IHttpContextAccessor>()?.HttpContext?.User;
            return user?.Identity?.IsAuthenticated == true ? user : null;
        };
        builder.Services.AddAuthorization();
    }
    else
    {
        // Auth is off by explicit configuration (BifrostQL:DisableAuth), so the MCP surface
        // is anonymous too — declared as such, which logs a startup warning, rather than
        // arriving there silently.
        mcpAuth.Mode = McpAuthMode.AnonymousDev;
    }

    builder.Services.AddBifrostMcpHttp(mcpAuth);
}

var app = builder.Build();
hostServices = app.Services;

if (app.Environment.IsDevelopment())
    app.UseDeveloperExceptionPage();

if (app.Environment.IsDevelopment())
{
    app.UseCors(x => x
        .AllowAnyMethod()
        .AllowAnyHeader()
        .AllowAnyOrigin());
}
else
{
    var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
    if (allowedOrigins != null && allowedOrigins.Length > 0)
    {
        app.UseCors(x => x
            .AllowAnyMethod()
            .AllowAnyHeader()
            .WithOrigins(allowedOrigins));
    }
}

// Dev identity stamp (opt-in): BifrostQL:DevIdentity = "<subject>". The chat
// endpoints (and anything else identity-gated) refuse anonymous callers, so
// local demos need SOME principal. This is a demo convenience, not an auth
// scheme: it refuses to run in Production outright, and it logs a warning at
// startup because stamping every request with a fixed identity is a posture
// change worth seeing in the logs. Real deployments use local auth, OIDC, or
// JWT bearer instead.
var devIdentity = app.Configuration["BifrostQL:DevIdentity"];
if (!string.IsNullOrWhiteSpace(devIdentity))
{
    if (app.Environment.IsProduction())
        throw new InvalidOperationException(
            "BifrostQL:DevIdentity stamps a fixed identity on every request and must not run in Production. " +
            "Configure real authentication instead.");
    app.Logger.LogWarning(
        "BifrostQL:DevIdentity is set: every request runs as '{Subject}'. Demo/development use only.",
        devIdentity);
    app.Use(async (context, next) =>
    {
        context.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                new[] { new System.Security.Claims.Claim("sub", devIdentity) },
                authenticationType: "BifrostDevIdentity"));
        await next();
    });
}

// Authentication middleware is added by UseBifrostQL when auth is enabled (IsUsingAuth), so
// it is not added a second time here — the earlier double UseAuthentication was redundant.
app.UseBifrostQL();

// LLM chat endpoints (opt-in): BifrostQL:Chat:Enabled = true. UseBifrostChat
// fails fast at startup when no Anthropic api key is configured.
var chatSection = app.Configuration.GetSection("BifrostQL:Chat");
if (chatSection.GetValue("Enabled", false))
{
    app.UseBifrostChat(chat =>
    {
        var systemPrompt = chatSection["SystemPrompt"];
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            chat.SystemPrompt = systemPrompt;
    });
}

if (mcpHttpEnabled)
{
    var mcpEndpoint = app.MapBifrostMcp(app.Configuration["BifrostQL:Mcp:Http:Path"] ?? "/mcp");
    if (authEnabled)
    {
        // MCP clients are API callers, so challenge the JWT scheme directly: the smart
        // scheme would forward a header-less request to cookie and answer an OIDC 302,
        // which no MCP client can follow. Unauthenticated callers get a 401 here and no
        // JSON-RPC session is created.
        app.UseAuthorization();
        mcpEndpoint.RequireAuthorization(new AuthorizeAttribute
        {
            AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme,
        });
    }
}

await app.RunAsync();

// Exposed for WebApplicationFactory<Program> in integration tests.
public partial class Program;
