using BifrostQL.Core;
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

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.MaxRequestHeadersTotalSize = 131072;
});

builder.Services.AddSingleton<BasicAuditModule>();
builder.AddBifrostQL(sp =>
{
    return new[] {
        sp.GetRequiredService<BasicAuditModule>(),
    };
});

builder.Services.AddCors();
var app = builder.Build();

app.UseDeveloperExceptionPage();
app.UseCors(x => x
    .AllowAnyMethod()
    .AllowAnyHeader()
    .AllowAnyOrigin());

if (jwtConfig.Exists())
    app.UseAuthentication();

app.UseBifrostQL();


await app.RunAsync();
