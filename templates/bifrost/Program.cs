using BifrostQL.Core.Model;
using BifrostQL.Server;
//#if (UseSqlServer)
using BifrostQL.SqlServer;
//#endif
//#if (UsePostgres)
using BifrostQL.Ngsql;
//#endif
//#if (UseMySql)
using BifrostQL.MySql;
//#endif
//#if (UseSqlite)
using BifrostQL.Sqlite;
//#endif
//#if (UseJwtAuth)
using Microsoft.AspNetCore.Authentication.JwtBearer;
//#endif

// Dialect packages do not register themselves. Without this line startup fails
// with "No factory registered for provider ...".
//#if (UseSqlServer)
DbConnFactoryResolver.Register(BifrostDbProvider.SqlServer, cs => new SqlServerDbConnFactory(cs));
//#endif
//#if (UsePostgres)
DbConnFactoryResolver.Register(BifrostDbProvider.PostgreSql, cs => new PostgresDbConnFactory(cs));
//#endif
//#if (UseMySql)
DbConnFactoryResolver.Register(BifrostDbProvider.MySql, cs => new MySqlDbConnFactory(cs));
//#endif
//#if (UseSqlite)
DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
//#endif

var builder = WebApplication.CreateBuilder(args);

//#if (UseJwtAuth)
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => builder.Configuration.Bind("JwtSettings", options));
//#endif

builder.Services.AddBifrostQL(options => options.BindStandardConfig(builder.Configuration));
builder.Services.AddCors();

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.UseDeveloperExceptionPage();

app.UseCors(x => x
    .AllowAnyMethod()
    .AllowAnyHeader()
    .AllowAnyOrigin());

//#if (UseJwtAuth)
// Authentication must run before UseBifrostQL, or BifrostQL sees every caller as anonymous.
app.UseAuthentication();
//#endif

app.UseBifrostQL();

await app.RunAsync();
