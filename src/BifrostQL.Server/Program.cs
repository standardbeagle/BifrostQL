using BifrostQL.Core;
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

builder.AddBifrostQL();
builder.Services.AddCors();
var app = builder.Build();

app.UseDeveloperExceptionPage();
app.UseCors(x => x
    .AllowAnyMethod()
    .AllowAnyHeader()
    .AllowAnyOrigin());

if (jwtConfig.Exists())
    app.UseAuthentication();

app.UseUiAuth("https://localhost:7077/callback");
app.UseBifrostQL();


await app.RunAsync();
