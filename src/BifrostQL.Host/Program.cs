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

// MCP over Streamable HTTP (opt-in): BifrostQL:Mcp:Http:Enabled = true.
// Default auth posture is FailClosed (empty user context; tenant-filtered reads
// refuse exactly like an unauthenticated GraphQL request); writes stay off.
var mcpHttpEnabled = builder.Configuration.GetValue("BifrostQL:Mcp:Http:Enabled", false);
if (mcpHttpEnabled)
    builder.Services.AddBifrostMcpHttp();

var app = builder.Build();

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
    app.MapBifrostMcp(app.Configuration["BifrostQL:Mcp:Http:Path"] ?? "/mcp");

await app.RunAsync();

// Exposed for WebApplicationFactory<Program> in integration tests.
public partial class Program;
