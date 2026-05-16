using BifrostQL.Core.AppMetadata;
using BifrostQL.Core.Model;
using BifrostQL.Core.Workflows;
using BifrostQL.Samples.HostedSpa;
using BifrostQL.Server;
using BifrostQL.Server.Auth;
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

// Local DB-backed auth against the Membership Manager app_users table. The MM schema
// names its key columns user_id/email and stores a denormalized delimited role list in
// a roles column, so the LocalAuthOptions defaults are overridden to match.
builder.Services.AddBifrostLocalAuth(connectionString!, auth =>
{
    auth.UserTable = "app_users";
    auth.IdColumn = "user_id";
    auth.LoginColumn = "email";
    auth.RolesColumn = "roles";
});

// Optional OIDC login (Google / Microsoft 365), added purely by configuration on top of
// local auth — inert by default. The sample ships with no "Oidc" config section, so this
// block is skipped and the sample runs on local auth only. A self-hosted club enables it
// by adding an "Oidc" section to appsettings.json with a real client id/secret; no code
// change is needed. The OIDC claim mappers normalize a Google / Microsoft 365 principal
// to the SAME AppIdentity (id/email/tenant/roles) LocalUserStore produces, so the policy
// engine and tenant filter behave identically — authorization semantics are unchanged.
var oidcConfig = builder.Configuration.GetSection("Oidc");
if (oidcConfig.Exists())
{
    builder.Services.AddBifrostOidcClaimMappers(mappers =>
    {
        var microsoftIssuer = oidcConfig["Microsoft365:Issuer"];
        if (!string.IsNullOrWhiteSpace(microsoftIssuer))
            mappers.AddMicrosoft365(microsoftIssuer);

        var googleIssuer = oidcConfig["Google:Issuer"];
        if (!string.IsNullOrWhiteSpace(googleIssuer))
            mappers.AddGoogle(googleIssuer);
    });
}

// App-metadata overlay: a standalone JSON file describing how the Membership
// Manager entities (members, households, household_members) present in a
// metadata-driven client. Served at /_app-metadata, independent of the schema.
var overlayPath = Path.Combine(
    builder.Environment.ContentRootPath, "membership-manager.appmetadata.json");
builder.Services.AddBifrostAppMetadata(new FileAppMetadataSource(overlayPath));
builder.Services.AddSingleton<IReadOnlyDictionary<string, WorkflowDefinition>>(_ =>
{
    var json = File.Exists(overlayPath) ? File.ReadAllText(overlayPath) : "{}";
    return WorkflowConfigCollector.FromJson(json);
});
builder.Services.AddSingleton<IWorkflowRunner>(sp =>
    new WorkflowRunner(
        sp.GetRequiredService<IReadOnlyDictionary<string, WorkflowDefinition>>(),
        sp.GetRequiredService<IWorkflowDataExecutor>()));

var app = builder.Build();

app.UseDeveloperExceptionPage();

// Local DB-backed auth is independent of the GraphQL JWT auth path (BifrostQL:DisableAuth
// stays true — this sample does not gate the GraphQL endpoint behind JWT). The cookie
// authentication scheme AddBifrostLocalAuth registered must still run so /auth/session
// sees the login cookie, so UseAuthentication is added explicitly here.
app.UseAuthentication();

// GraphQL endpoint first so the SPA fallback does not shadow it.
app.UseBifrostQL();

// Local auth login/logout/session endpoints, mapped after the authentication
// middleware so the issued cookie is honored on /auth/session.
app.UseBifrostLocalAuth();

// Membership Manager sidecar workflow endpoints (record-payment, renew).
// Mapped after UseBifrostQL so they sit alongside /graphql and before the SPA
// fallback claims the route. Each endpoint orchestrates Bifrost mutations
// through the standard pipeline — see MembershipWorkflowEndpoints.
app.MapMembershipWorkflows();

// App-metadata overlay endpoint (/_app-metadata) before the SPA fallback.
app.UseBifrostAppMetadata();

// Static SPA assets plus an index.html route fallback. The GraphQL playground
// lives at /playground and the sidecar workflow endpoints under /workflows, so
// both are excluded from the SPA fallback alongside the defaults
// (/graphql, /api, /health) — otherwise the fallback would shadow them.
app.UseBifrostSpa(spa => spa
    .AddExcludedPathPrefix("/playground")
    .AddExcludedPathPrefix("/workflows"));

await app.RunAsync();

// Exposed so WebApplicationFactory<Program> can host this sample in integration tests.
public partial class Program;
