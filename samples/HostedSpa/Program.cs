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
// without any external database setup. The path is derived from the configured
// connection string so the seeded file and the database BifrostQL opens always
// agree (this also lets integration tests point the sample at a throwaway file).
var connectionString = builder.Configuration.GetConnectionString("bifrost");
var dbPath = SampleDatabase.ResolveDbPath(connectionString, builder.Environment.ContentRootPath);
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

// Exposed so WebApplicationFactory<Program> can host this sample in integration tests.
public partial class Program;
