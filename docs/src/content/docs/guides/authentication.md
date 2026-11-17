---
title: "GraphQL API Authentication and OIDC"
description: "Set up local user login, OIDC providers, and JWT bearer tokens, and see how every BifrostQL transport shares one identity contract before authorization runs."
---

BifrostQL supports three authentication paths: local DB-backed user login, OIDC providers (Microsoft 365 and Google), and raw JWT bearer tokens. Every path produces the same provider-agnostic identity, which drives tenant isolation, audit column population, and any custom modules that depend on user context.

This guide covers identity — establishing *who* the caller is. What that identity may then read or write is a separate layer: see [Row and Column Authorization Policies](/BifrostQL/guides/authorization/).

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
app.UseBifrostLocalAuth();   // maps /auth/login, /auth/logout, /auth/session, /auth/password
app.UseBifrostQL();
await app.RunAsync();
```

`UseBifrostLocalAuth()` maps the login and logout endpoints. Call it after `UseAuthentication()` so the issued cookie is honored on subsequent requests.

### 2. Endpoints

| Endpoint | Method | Behavior |
|----------|--------|----------|
| `/auth/login` | `POST` | Accepts `{ "login": "...", "password": "..." }`. On valid credentials, opens a server-side session, issues its cookie and returns `204`. A missing user, wrong password, disabled account and locked account all return the same `401` with the same body, so the response does not say which. A correct but expired password (`MaxPasswordAge`) returns `403` `{"error":"password_expired"}` and opens no session. |
| `/auth/logout` | `POST` | Revokes the server-side session, clears the cookie and returns `204`. A copied cookie stops working too. |
| `/auth/password` | `POST` | Accepts `{ "login", "currentPassword", "newPassword" }`. The current password is checked like a login (rate windows, lockout, audit); an expired one is accepted. A new password that breaks the policy returns `400` `{"error":"password_policy","rule":"..."}`, where the rule is `min_length`, `max_length`, `letter_and_digit` or `reused`. On success every session of the user is revoked and `204` is returned; the user signs in again. |
| `/auth/admin/revoke-sessions`, `/auth/admin/unlock` | `POST` | Mapped only when `AdminRole` is set. JSON body `{ "userId": "..." }`; the caller must hold `AdminRole`. Revokes every session of the user, or ends a lockout. Host code can call the same actions on `LocalAuthAdministration`. |

Passwords are verified with the vetted ASP.NET Core `PasswordHasher` (PBKDF2-HMAC-SHA512, `PasswordHashIterations`, default 210,000); no plaintext password is ever stored or compared.

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

### Login throttling and session lifetime

Login attempts are counted per source IP and per account before the password is checked, so a flood of guesses never reaches the hash verifier. Over either cap the endpoint answers `429` with the same text whether or not the account exists:

| Option | Default | Effect |
|---|---|---|
| `MaxLoginAttemptsPerSource` | `20` | Attempts one IP may make per window, across all accounts. |
| `MaxLoginAttemptsPerAccount` | `10` | Attempts against one login per window, from any IP. |
| `LoginAttemptWindow` | `5 min` | The window both caps count over. |
| `SessionLifetime` | `8 h` | Absolute session lifetime from sign-in. Activity does not extend it. |

A successful login whose stored hash uses outdated parameters is rehashed in place.

### Sessions, lockout and password policy

Each sign-in opens a row in `_bifrost_auth_sessions`, and every authenticated request checks it. The cookie carries a random session id; the table keeps only its SHA-256. The state tables are created on first use in the app-user database and are never served by a GraphQL endpoint. Register your own `ILocalAuthStateStore` before `AddBifrostLocalAuth` to keep them elsewhere.

| Option | Default | Effect |
|---|---|---|
| `SessionIdleTimeout` | `15 min` | A session with no request for this long is revoked. |
| `SessionRevalidationInterval` | `1 min` | How often a session re-reads its user row. A deleted or disabled user, a changed password, or changed roles, tenant or household revoke every session of that user. A display-name change only refreshes the claims. |
| `MaxConcurrentSessions` | none | A sign-in past the cap revokes the user's oldest sessions. |
| `DisabledColumn` | none | Column on the user table; a true or non-zero value blocks sign-in and revokes sessions. |
| `LockoutThreshold` | `10` | Consecutive failed logins (or password-change attempts) that lock the account. |
| `LockoutDuration` | `30 min` | How long the lock lasts; `null` locks until an administrator unlocks. |
| `MinPasswordLength` / `MaxPasswordLength` | `12` / `256` | Length bounds for a new password, in characters. |
| `PasswordRequiresLetterAndDigit` | `true` | A new password needs a letter and a digit. |
| `PasswordHistoryCount` | `4` | A new password may not match the current one or the last N. |
| `MaxPasswordAge` | none | A password older than this cannot sign in until changed. An account with no recorded change counts as expired. |
| `AdminRole` / `AdminPath` | none / `/auth/admin` | Enables the administration endpoints. |

Settings are validated at `AddBifrostLocalAuth`; an out-of-range value throws at startup.

Every login, logout, lock, unlock, password change, revocation and privilege change is written to `_bifrost_auth_audit`, logged under the `BifrostQL.Audit.Authentication` category, and passed to every registered `IAuthAuditSink`. See [Compliance controls](/BifrostQL/guides/compliance-controls/) for how these map to HIPAA, SOX and PCI DSS.

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

### Onboarding path: start local, add OIDC later by configuration

A self-hosted deployment — for example a club running the Membership Manager app — can start with local DB-backed login alone and add Google or Microsoft 365 login later **purely by configuration, with no code change to the app's authorization rules**.

1. **Start with local auth.** `AddBifrostLocalAuth` against the app-user table is enough to sign users in. Tenant isolation, audit columns, and policy checks all work off the `AppIdentity` local login produces.
2. **Add OIDC when you want it.** Register the claim mappers with `AddBifrostOidcClaimMappers` and add the matching `AddOpenIdConnect` client configuration. Gate it behind a config section so the wiring stays inert until a real issuer and client secret are supplied — see the [HostedSpa sample](https://github.com/standardbeagle/BifrostQL/tree/main/samples/HostedSpa), which ships the OIDC block commented out and runs on local auth only by default.
3. **Authorization semantics are unchanged.** Both paths converge on the same `AppIdentity`, and `IdentityContextMapper` projects either one into the **identical** `UserContext` keys — the tenant key, the roles key, and the audit user key. The policy engine, `TenantFilterTransformer`, and role-bypass checks read those keys and never see which provider authenticated the request.

The claim-shape guarantee is enforced by tests, not just documented: `OidcLocalAuthParityTests` verifies that a Microsoft 365 OIDC principal and a `LocalUserStore` login for the same logical user produce the same tenant and roles `UserContext` values — including after the OIDC identity is re-issued in the shared local-auth cookie shape. So adding OIDC cannot silently change who can see or do what; if a mapper ever diverged from local auth's claim shape, that test would fail.

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

The `AddAuthentication` call may come before or after `AddBifrostQL`. BifrostQL registers its own
cookie and OIDC login, but it only fills in the default schemes your host left unset, so your
`JwtBearerDefaults.AuthenticationScheme` default stays the scheme that authenticates `/graphql`.

### Which routes the login gate covers

With auth enabled, `UseBifrostQL()` puts a login gate in the pipeline: an unauthenticated request
gets `401` (API clients) or an OIDC login redirect (browsers). By default the gate covers **every
request that reaches it**, including routes your host maps for itself, such as a health check.

To gate only BifrostQL's own GraphQL endpoint and playground, pass the scope explicitly:

```csharp
app.UseBifrostQL(BifrostAuthGateScope.BifrostRoutes);
app.MapGet("/health", () => "ok");                    // reachable anonymously
app.MapPost("/billing", Bill).RequireAuthorization(); // your route, your authorization
```

`BifrostRoutes` widens what an anonymous caller can reach. Every route your host maps must then
declare its own authorization. Choose it deliberately.

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

## Loading grants per request (`IGrantResolver`)

Claims baked into a token live as long as the token does — a JWT issued for weeks still carries the permissions the user had at sign-in. When your capability set lives in the database and a permission change must take effect *when made*, register a grant resolver. It runs **once per request**, for authenticated principals, at the single point every transport assembles the user context, and its result is **unioned** into the `permissions` context key before any policy, tenant, or filter module reads the context.

Two transport-shape exceptions to "once per request":

- **pgwire, RESP, and LDAP project identity once per connection, at login.** The resolver runs at that moment; a permission change takes effect on the client's *next connection*, not mid-session.
- **The Prometheus scrape endpoint never runs it** — its fixed service identity is projected without a request service provider, so no scoped resolver can run there.

```csharp
builder.Services.AddBifrostGrantResolver(async (identity, sp, ct) =>
{
    // Track shape: the user's profile row carries a text[] of capabilities.
    await using var conn = sp.GetRequiredService<NpgsqlDataSource>().CreateConnection();
    return await conn.QuerySingleAsync<string[]>(
        "SELECT capabilities FROM permission_profiles WHERE user_id = @id",
        new { id = identity.Id }) ?? Array.Empty<string>();
});
```

Or as a scoped service: `builder.Services.AddBifrostGrantResolver<MyGrantResolver>()` where `MyGrantResolver : IGrantResolver`.

Contract:

- **A per-request DB read is the expected shape.** Caching (and its revocation policy) is the application's business — if you cache, you decide how stale a grant may be. The user-context assembly contract is synchronous, so the hook's async signature is awaited inline on the request path (it blocks a pool thread for the duration of the read); keep the read bounded.
- The result is a *union* with the identity's mapped permissions, never a replacement. A `null` result is treated as empty: the identity's own permissions remain.
- **Fail closed.** A resolver that throws empties the permission set for that request — the identity's pre-resolver `permissions` are wiped too — and logs a `Warning` naming the identity id. The exception never reaches the caller; where a policy needs the missing grant the answer is a policy deny, not a server error. Only `permissions` is wiped: token `roles` are claims of the token itself, not resolver-sourced grants, and remain.
- `permissions` is an identity-owned context key: a client-supplied (wire) context value can never add to it. The resolver is the only way grants enter beyond the authenticated identity itself.

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
