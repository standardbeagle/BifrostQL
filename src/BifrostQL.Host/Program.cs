using BifrostQL.Core.Modules;
using BifrostQL.Server;
using Microsoft.AspNetCore.Authentication.JwtBearer;

var builder = WebApplication.CreateBuilder(args);

var jwtConfig = builder.Configuration.GetSection("JwtSettings");
if (jwtConfig.Exists())
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme,
        options => builder.Configuration.Bind("JwtSettings", options));
}

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

if (jwtConfig.Exists())
    app.UseAuthentication();

app.UseBifrostQL();


await app.RunAsync();

// Exposed for WebApplicationFactory<Program> in integration tests.
public partial class Program;
