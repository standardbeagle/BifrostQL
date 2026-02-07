using BifrostQL.Core.Modules;
using BifrostQL.Server;
//#if (UseJwtAuth)
using Microsoft.AspNetCore.Authentication.JwtBearer;
//#endif

var builder = WebApplication.CreateBuilder(args);

//#if (UseJwtAuth)
var jwtConfig = builder.Configuration.GetSection("JwtSettings");
if (jwtConfig.Exists())
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme,
        options => builder.Configuration.Bind("JwtSettings", options));
}
//#endif

builder.Services.AddSingleton<BasicAuditModule>();
builder.Services.AddBifrostQL(options =>
{
    options
        .BindStandardConfig(builder.Configuration)
        .AddModules(sp => new[] {
            sp.GetRequiredService<BasicAuditModule>(),
        });
});

builder.Services.AddCors();
var app = builder.Build();

app.UseDeveloperExceptionPage();
app.UseCors(x => x
    .AllowAnyMethod()
    .AllowAnyHeader()
    .AllowAnyOrigin());

//#if (UseJwtAuth)
if (jwtConfig.Exists())
    app.UseAuthentication();
//#endif

app.UseBifrostQL();

await app.RunAsync();
