using BifrostQL.Core.Model;
using BifrostQL.Samples.HostedSpa;
using BifrostQL.Server;
using BifrostQL.Sqlite;

// Sample: a Vite-built SPA and a BifrostQL GraphQL API served from one ASP.NET process.
// The SPA calls a same-origin "/graphql" endpoint, so there is no CORS configuration.

var builder = WebApplication.CreateBuilder(args);

// Register the SQLite dialect factory so DbConnFactoryResolver can route to it.
DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));

// Create a small SQLite database the first time the sample runs so it works
// without any external database setup.
var dbPath = Path.Combine(builder.Environment.ContentRootPath, "hostedspa-sample.db");
SampleDatabase.EnsureCreated(dbPath);

builder.Services.AddBifrostQL(options =>
    options.BindStandardConfig(builder.Configuration));

var app = builder.Build();

app.UseDeveloperExceptionPage();

// GraphQL endpoint first so the SPA fallback does not shadow it.
app.UseBifrostQL();

// Static SPA assets plus an index.html route fallback. The GraphQL playground lives
// at /playground, so it is excluded from the SPA fallback alongside the defaults
// (/graphql, /api, /health).
app.UseBifrostSpa(spa => spa.AddExcludedPathPrefix("/playground"));

await app.RunAsync();
