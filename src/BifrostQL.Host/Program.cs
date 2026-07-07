using BifrostQL.Core.Model;
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

// Authentication middleware is added by UseBifrostQL when auth is enabled (IsUsingAuth), so
// it is not added a second time here — the earlier double UseAuthentication was redundant.
app.UseBifrostQL();


await app.RunAsync();

// Exposed for WebApplicationFactory<Program> in integration tests.
public partial class Program;
