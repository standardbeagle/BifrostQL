---
title: Authentication
description: Local-user login, OIDC providers, JWT bearer tokens, and the shared identity contract in BifrostQL.
---

BifrostQL supports three authentication paths: local DB-backed user login, OIDC providers (Microsoft 365 and Google), and raw JWT bearer tokens. Every path produces the same provider-agnostic identity, which drives tenant isolation, audit column population, and any custom modules that depend on user context.

## The shared identity contract

No matter how a user signs in, the authentication layer produces an `AppIdentity` — a provider-neutral record consumed by the security modules. Local login, OIDC, and JWT all converge on this one shape, so a module never has to know which provider authenticated the request.

`AppIdentity` carries:

| Field | Purpose |
|-------|---------|
| `Id` | Stable, provider-neutral user identifier |
| `Email` | User's email address, if known |
| `DisplayName` | Human-readable name, if known |
| `Provider` | Which provider produced the identity (`local`, `oidc:microsoft365`, `oidc:google`) |
| `TenantId` | Primary tenant identifier for tenant isolation, if the user belongs to one tenant |
| `OrgIds` | All organization/group identifiers the user belongs to |
| `Roles` | Roles granted to the user |
| `Claims` | Additional provider claims, copied verbatim into the user context |

`OrgIds`, `Roles`, and `Claims` are never null — they default to empty collections, so consumers never need to null-check them.

`IdentityContextMapper` projects an `AppIdentity` into the `UserContext` dictionary that modules read. It writes three mapped keys:

| Mapped key | Default | Source field | Read by |
|------------|---------|--------------|---------|
| Tenant key | `tenant_id` | `AppIdentity.TenantId` | `TenantFilterTransformer` |
| Roles key | `roles` | `AppIdentity.Roles` | `AutoFilterTransformer` (bypass-role checks) |
| Audit user key | `id` | `AppIdentity.Id` | `BasicAuditModule` |

Provider claims are copied into the dictionary first, so the mapped identity keys above always take precedence over a same-named provider claim.

## Local-user login

For self-hosted deployments, BifrostQL can authenticate users against an app-user table in the same database it already serves. Credentials are verified server-side and never leave the server — only a session cookie is returned to the client.

### 1. Register local auth

`AddBifrostLocalAuth` adds cookie authentication and the user store. The app-user table is reached through a server-side connection factory built from the connection string you pass — typically the same `bifrost` connection string the GraphQL endpoint uses.

```csharp
using BifrostQL.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddBifrostLocalAuth(
    builder.Configuration.GetConnectionString("bifrost")!);

builder.Services.AddBifrostQL(o => o.BindStandardConfig(builder.Configuration));

var app = builder.Build();
app.UseAuthentication();
app.UseBifrostLocalAuth();   // maps /auth/login and /auth/logout
app.UseBifrostQL();
await app.RunAsync();
```

`UseBifrostLocalAuth()` maps the login and logout endpoints. Call it after `UseAuthentication()` so the issued cookie is honored on subsequent requests.

### 2. Endpoints

| Endpoint | Method | Behavior |
|----------|--------|----------|
| `/auth/login` | `POST` | Accepts `{ "login": "...", "password": "..." }`. On valid credentials, issues a session cookie and returns `204`. On a missing user or wrong password, returns `401` — the response is identical for both so account existence is never leaked. |
| `/auth/logout` | `POST` | Clears the session cookie and returns `204`. |

Passwords are verified with the vetted ASP.NET Core `PasswordHasher`; no plaintext password is ever stored or compared.

### 3. App-user table configuration

The table and column names are configurable so local auth can point at whatever schema your app-user rows use. Pass a configuration callback to `AddBifrostLocalAuth`:

```csharp
builder.Services.AddBifrostLocalAuth(
    builder.Configuration.GetConnectionString("bifrost")!,
    options =>
    {
        options.UserTable = "app_users";          // default: app_users
        options.LoginColumn = "email";            // default: email
        options.IdColumn = "id";                  // default: id
        options.PasswordHashColumn = "password_hash"; // default: password_hash
        options.DisplayNameColumn = "display_name";   // default: display_name
        options.TenantColumn = "tenant_id";       // default: tenant_id
        options.RolesColumn = "roles";            // default: roles (delimited list, e.g. admin,editor)
        options.LoginPath = "/auth/login";        // default: /auth/login
        options.LogoutPath = "/auth/logout";      // default: /auth/logout
    });
```

A successful login produces an `AppIdentity` with `Provider` set to `local`, the email as the login name, and roles parsed from the delimited roles column.

## OIDC providers (Microsoft 365 and Google)

BifrostQL normalizes authenticated OIDC principals into the same `AppIdentity` contract local auth produces. Each provider ships a claim mapper that knows which raw claim types carry the subject, email, name, tenant, and group memberships.

### 1. Register the claim mappers

`AddBifrostOidcClaimMappers` registers a mapper per OIDC provider, keyed by the issuer URL the provider stamps into the `iss` claim. `UseUiAuth()` selects the mapper by issuer and re-issues the cookie in the shared claim shape. Pair this with the `AddOpenIdConnect` wiring configured by `AddBifrostQL`.

```csharp
builder.Services.AddBifrostOidcClaimMappers(mappers =>
{
    mappers.AddMicrosoft365("https://login.microsoftonline.com/<tenant-id>/v2.0");
    mappers.AddGoogle("https://accounts.google.com");
});
```

### 2. Provider claim mappings

Each provider has a default mapping for which raw claim type supplies the tenant and group memberships:

| Provider | Tenant claim | Groups claim |
|----------|--------------|--------------|
| Microsoft 365 | `tid` → `TenantId` | `groups` → `OrgIds` |
| Google | none by default | none by default |

Google issues no tenant claim by default, so a Google identity has no `TenantId` unless you supply a custom mapping. A deployment that wants Workspace-domain isolation can point the mapper at Google's hosted-domain claim (`hd`) or an app-specific org claim:

```csharp
mappers.AddGoogle(
    "https://accounts.google.com",
    new OidcClaimMapping { TenantClaimType = "hd" });
```

The same `OidcClaimMapping` override works for Microsoft 365 when a deployment presents a custom claim shape.

Subject, email, and name are resolved provider-neutrally — the mapper reads the standard OIDC claim types with ASP.NET-mapped fallbacks, so mapping works whether or not ASP.NET's inbound claim mapping has rewritten the claim types.

## JWT bearer tokens

BifrostQL also supports OAuth2/OIDC via raw JWT bearer tokens, without the cookie re-issue path.

### 1. Configure JWT settings

Add your identity provider settings to `appsettings.json`:

```json
{
  "JwtSettings": {
    "Authority": "https://your-idp.com",
    "Audience": "your-api"
  },
  "BifrostQL": {
    "DisableAuth": false
  }
}
```

### 2. Wire up authentication middleware

Add JWT bearer authentication before BifrostQL in your `Program.cs`:

```csharp
using BifrostQL.Server;
using Microsoft.AspNetCore.Authentication.JwtBearer;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o => builder.Configuration.Bind("JwtSettings", o));

builder.Services.AddBifrostQL(o => o.BindStandardConfig(builder.Configuration));

var app = builder.Build();
app.UseAuthentication();
app.UseBifrostQL();
await app.RunAsync();
```

Order matters: `UseAuthentication()` must come before `UseBifrostQL()`. Otherwise, BifrostQL won't have access to the authenticated user.

## User context

BifrostQL builds a `BifrostContext` from the authenticated user. This context is available to all modules and transformers, and exposes a `UserContext` dictionary keyed by claim names. Modules read from this dictionary to populate filters and audit columns.

### Key claims

| Claim | Used by | Default key |
|-------|---------|-------------|
| Tenant ID | `TenantFilterTransformer` | `tenant_id` |
| User audit key | `BasicAuditModule` | configured by `user-audit-key` |
| Arbitrary row filters | `AutoFilterTransformer` | configured by `auto-filter` |

### Changing claim keys

The tenant and audit claim keys can be overridden via model metadata. These keys are honored regardless of which authentication path produced the identity — local, OIDC, and JWT all flow through the same `IdentityContextMapper`:

```
":root { tenant-context-key: org_id; user-audit-key: sub; }"
```

This tells the tenant filter transformer to read `org_id` from the user context instead of `tenant_id`, and tells audit population to use the `sub` claim as the user key.

For additional row-level filters, map columns to claims with `auto-filter`:

```
"dbo.orders { auto-filter: organization_id:org_id,region_id:region; }"
```

## Disabling authentication

For development and testing, set `DisableAuth` to `true`:

```json
{
  "BifrostQL": {
    "DisableAuth": true
  }
}
```

With auth disabled, all requests are treated as unauthenticated. Modules that depend on user context (tenant isolation, audit columns) will not function.

## CORS

If your GraphQL client runs in a browser on a different origin, configure CORS:

```csharp
builder.Services.AddCors();

var app = builder.Build();
app.UseCors(x => x.AllowAnyMethod().AllowAnyHeader().AllowAnyOrigin());
app.UseAuthentication();
app.UseBifrostQL();
```

For production, restrict the allowed origins:

```csharp
app.UseCors(x => x
    .WithOrigins("https://your-app.com")
    .AllowAnyMethod()
    .AllowAnyHeader()
    .AllowCredentials());
```

When you use cookie-based local auth or the OIDC cookie re-issue path from a browser on a different origin, the client must send credentials and CORS must allow them — use `.AllowCredentials()` with explicit origins (it cannot be combined with `.AllowAnyOrigin()`).
