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

`IdentityContextMapper` projects an `AppIdentity` into the `UserContext` dictionary that modules read. It writes these keys, always under these names:

| Key | Source field | Shape |
|-----|--------------|-------|
| `tenant_id` | `AppIdentity.TenantId` | string; omitted when the identity has no tenant |
| `tenant_ids` | `AppIdentity.OrgIds` | list of strings |
| `user_id` | `AppIdentity.Id` | string |
| `id` | `AppIdentity.Id` | string |
| `roles` | `AppIdentity.Roles` | list of strings |
| `permissions` | `AppIdentity.Permissions` | list of strings |

Provider claims (`AppIdentity.Claims`) are copied into the dictionary first, so the mapped identity keys above always take precedence over a same-named provider claim.

For an HTTP caller the user context also carries every other claim of the authenticated principal, under its claim type, as a **string array**. Keys compare case-insensitively: claim types that differ only in case (`groups` and `Groups`) share one key holding all their values, and a raw claim never replaces a key already present in another case (a raw `User` claim beside the `user` principal is dropped). A claim whose type is one of the keys above is never copied, even when the mapper left that key empty: a token's raw `tenant_id` claim does not give a tenant-less identity a tenant, and the tenant filter refuses that caller. `tenant-context-key` and `user-audit-key` name a key in this dictionary; they do not rename the mapper's keys (see [Changing claim keys](#changing-claim-keys)).

## Local-user login

For self-hosted deployments, BifrostQL can authenticate users against an app-user table in the same database it already serves. Credentials are verified server-side and never leave the server — only a session cookie is returned to the client.

### 1. Register local auth

`AddBifrostLocalAuth` adds cookie authentication and the user store. The app-user table is reached through a server-side connection factory built from the connection string you pass — typically the same `bifrost` connection string the GraphQL endpoint uses.

```csharp
using BifrostQL.Core.Crypto;
using BifrostQL.Server;

var builder = WebApplication.CreateBuilder(args);

// Local auth requires a TOTP second factor by default. Its secrets are sealed with field
// encryption, so the host supplies a root key and a durable data-key store.
builder.Services.AddSingleton<IRootKeyProvider>(
    ConfigRootKeyProvider.FromBase64(builder.Configuration["Bifrost:RootKey"]!));
builder.Services.AddSingleton<IDataEncryptionKeyStore>(
    new FileDataEncryptionKeyStore("/var/lib/bifrost/keys"));

builder.Services.AddBifrostLocalAuth(
    builder.Configuration.GetConnectionString("bifrost")!,
    options => options.MfaKeyRef = "config:mfa");

builder.Services.AddBifrostQL(o => o.BindStandardConfig(builder.Configuration));

var app = builder.Build();
app.UseAuthentication();
app.UseBifrostLocalAuth();   // maps /auth/login, /auth/logout, /auth/session, /auth/password, /auth/mfa/*
app.UseBifrostQL();
await app.RunAsync();
```

`UseBifrostLocalAuth()` maps the login and logout endpoints. Call it after `UseAuthentication()` so the issued cookie is honored on subsequent requests.

Without `MfaKeyRef`, `AddBifrostLocalAuth` throws at startup while `RequireMfa` is on (the default). A deployment that does not need a second factor opts out explicitly with `options.RequireMfa = false`; see [Multi-factor authentication](#multi-factor-authentication).

### 2. Endpoints

| Endpoint | Method | Behavior |
|----------|--------|----------|
| `/auth/login` | `POST` | Accepts `{ "login": "...", "password": "..." }`. On valid credentials, opens a server-side session, issues its cookie and returns `204`. When a second factor is owed it returns `200` instead: `{"status":"mfa-required","challenge":"...","expiresAt":"..."}` with no cookie, or `{"status":"mfa-enrollment-required"}` with an enrollment-only cookie (see [Multi-factor authentication](#multi-factor-authentication)). A missing user, wrong password, disabled account and locked account all return the same `401` with the same body, so the response does not say which. A correct but expired password (`MaxPasswordAge`) returns `403` `{"error":"password_expired"}` and opens no session. |
| `/auth/logout` | `POST` | Revokes the server-side session, clears the cookie and returns `204`. A copied cookie stops working too. |
| `/auth/password` | `POST` | Accepts `{ "login", "currentPassword", "newPassword" }`, plus `"code"` or `"recoveryCode"` when the account has a second factor (else `401`). The current password is checked like a login (rate windows, lockout, audit); an expired one is accepted. A new password that breaks the policy returns `400` `{"error":"password_policy","rule":"..."}`, where the rule is `min_length`, `max_length`, `letter_and_digit` or `reused`. On success every session of the user is revoked and `204` is returned; the user signs in again. |
| `/auth/admin/revoke-sessions`, `/auth/admin/unlock`, `/auth/admin/reset-mfa` | `POST` | Mapped only when `AdminRole` is set. JSON body `{ "userId": "..." }`; the caller must hold `AdminRole`. Revokes every session of the user, ends a lockout, or removes the user's second factor (and revokes their sessions). Host code can call the same actions on `LocalAuthAdministration`. |

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
        options.MfaKeyRef = "config:mfa";         // required while RequireMfa is on (the default)
    });
```

A successful login produces an `AppIdentity` with `Provider` set to `local`, the email set to the login column's stored value (not the caller's spelling of it), and roles parsed from the delimited roles column.

### Login throttling and session lifetime

Login attempts are counted per source IP and per account before the password is checked, so a flood of guesses never reaches the hash verifier. Over either cap the endpoint answers `429` with the same text whether or not the account exists, and audits the refusal (reason `rate-limited`, no subject, never the login name typed):

| Option | Default | Effect |
|---|---|---|
| `MaxLoginAttemptsPerSource` | `20` | Attempts one IP may make per window, across all accounts. |
| `MaxLoginAttemptsPerAccount` | `10` | Attempts against one login per window, from any IP. |
| `LoginAttemptWindow` | `5 min` | The window both caps count over. |
| `SessionLifetime` | `8 h` | Absolute session lifetime from sign-in. Activity does not extend it. At most 3650 days. |

A successful login whose stored hash uses outdated parameters is rehashed in place, unless the account is locked. The rehash only replaces the hash it just verified and keeps the user's other sessions. For a user with a second factor it happens at the password step, before the challenge ticket is issued, since the password is not available at step two; the ticket binds the upgraded hash's stamp.

### Sessions, lockout and password policy

Each sign-in opens a row in `_bifrost_auth_sessions`, and every authenticated request checks it. Long-lived connections check it too: the binary WebSocket before every message (an ended session closes it with PolicyViolation), and a `/_chat` stream before each tool call (an ended session ends the stream with a `session-ended` error event). The cookie carries a random session id; the table keeps only its SHA-256. The state tables are created on first use in the app-user database and are never served by a GraphQL endpoint, except through `_rawQuery`, which runs caller SQL and is opt-in (`raw-sql: enabled`) and role-gated (`raw-sql-role`). Register your own `ILocalAuthStateStore` before `AddBifrostLocalAuth` to keep them elsewhere.

| Option | Default | Effect |
|---|---|---|
| `SessionIdleTimeout` | `15 min` | A session with no request for this long is revoked. |
| `SessionRevalidationInterval` | `1 min` | How often a session re-reads its user row. A deleted or disabled user, a changed password, or changed roles, tenant or household revoke every session of that user. A display-name change only refreshes the claims. |
| `MaxConcurrentSessions` | none | A sign-in past the cap revokes the user's oldest sessions. |
| `DisabledColumn` | none | Column on the user table; a true or non-zero value blocks sign-in and revokes sessions. |
| `LockoutThreshold` | `10` | Consecutive failed logins (or password-change attempts) that lock the account. |
| `LockoutDuration` | `30 min` | How long the lock lasts, at most 3650 days; `null` locks until an administrator unlocks. |
| `MinPasswordLength` / `MaxPasswordLength` | `12` / `256` | Length bounds for a new password, in characters. |
| `PasswordRequiresLetterAndDigit` | `true` | A new password needs a letter and a digit. |
| `PasswordHistoryCount` | `4` | A new password may not match the current one or the N before it. |
| `MaxPasswordAge` | none | A password older than this cannot sign in until changed. An account with no recorded change counts as expired. |
| `AdminRole` / `AdminPath` | none / `/auth/admin` | Enables the administration endpoints. |

Settings are validated at `AddBifrostLocalAuth`; an out-of-range value throws at startup. The login, password-change and administration endpoints answer `415` to a body that is not JSON, and the administration endpoints answer `400` to a `userId` longer than 128 characters.

Every login, logout, lock, unlock, password change, revocation, privilege change and MFA event (enroll, enable, disable, verify success and failure, recovery-code use and regeneration, administrator reset) is written to `_bifrost_auth_audit`, logged under the `BifrostQL.Audit.Authentication` category, and passed to every registered `IAuthAuditSink`. See [Compliance controls](/BifrostQL/guides/compliance-controls/) for how these map to HIPAA, SOX and PCI DSS.

### Multi-factor authentication

Local auth has a TOTP second factor (RFC 6238: SHA-1, 30-second step, 6 digits by default), and requires it of every user unless you opt out.

| Option | Default | Effect |
|---|---|---|
| `RequireMfa` | `true` | Every local user must present a second factor. A user not yet enrolled gets an enrollment-only session. |
| `RequireMfaForRoles` | empty | With `RequireMfa = false`, require it only of users holding one of these roles. |
| `MfaKeyRef` | none | Field-encryption key-ref (e.g. `config:mfa`) that seals TOTP secrets. Needs an `IRootKeyProvider` and a durable `IDataEncryptionKeyStore`. Null turns the MFA endpoints off; startup fails if `RequireMfa` or `RequireMfaForRoles` asks for MFA without it. |
| `MfaPath` | `/auth/mfa` | Base path of the MFA endpoints. |
| `MfaIssuer` | `BifrostQL` | Issuer shown in authenticator apps. |
| `MfaTotpAlgorithm` / `MfaTotpDigits` / `MfaTotpPeriodSeconds` | `Sha1` / `6` / `30` | TOTP parameters for new enrollments. Most authenticator apps support only the defaults. |
| `MfaChallengeLifetime` | `5 min` | How long a login challenge ticket can be answered. |
| `MaxMfaChallengeAttempts` | `5` | Wrong codes that spend a challenge ticket. |
| `MfaEnrollmentTimeout` | `10 min` | How long an unconfirmed enrollment, and an enrollment-only session, last. |
| `MaxMfaAttemptsPerSource` / `MaxMfaAttemptsPerAccount` | `20` / `10` | Code attempts per `LoginAttemptWindow`, counted before the code is checked; over either cap answers `429`. |

**Sign-in.** For an enrolled user, a correct password returns `mfa-required` and a challenge ticket. The client posts `{ "challenge": "...", "code": "123456" }` (or `"recoveryCode"`) to `/auth/mfa/verify`, which opens the session and returns `204`. A wrong password still returns the same bare `401`, so it does not reveal whether the account has MFA. A wrong or replayed code returns `401` and counts toward the lockout; an expired, spent or unknown ticket returns `401` `{"error":"mfa_challenge_invalid"}` (sign in again).

**Enrollment.** All MFA endpoints are `POST` with a JSON body.

| Endpoint | Session | Body | Result |
|---|---|---|---|
| `/auth/mfa/enroll` | any, including enrollment-only | `{}` | `{ "secret", "otpAuthUri", "expiresAt", "algorithm", "digits", "period" }`. Render `otpAuthUri` as a QR code, or let the user type `secret`. `409` if MFA is already on. |
| `/auth/mfa/enroll/confirm` | any, including enrollment-only | `{ "code" }` | Turns MFA on and returns `{ "recoveryCodes": [10 codes] }`, shown this once. Revokes every session of the user and signs the caller in again with a full session. |
| `/auth/mfa/recovery-codes` | full | `{ "code" }` or `{ "recoveryCode" }` | Replaces all recovery codes; returns the new ones. Revokes every other session. |
| `/auth/mfa/disable` | full | `{ "code" }` or `{ "recoveryCode" }` | Removes the second factor, revokes every session including the caller's. |

**Enforcement.** An enrollment-only session counts as signed in only on `/auth/mfa/enroll`, `/auth/mfa/enroll/confirm` and the logout path; every other request, GraphQL included, runs anonymous. The binary WebSocket (`/bifrost-ws`) closes with PolicyViolation at the next message, and a `/_chat` stream ends with `session-ended` before its next tool call. A password-only session of a user the requirement covers (for example, one issued before the role was added to `RequireMfaForRoles`) is revoked at its next check.

**Wrong passwords.** The second-factor row is read only after the password verifies, so a wrong password answers the same bare `401`, with the same work, whether or not the account has a second factor. For an enrolled user the failed-attempt count clears only when the second factor passes.

**Claims.** The cookie carries an `amr` claim per method (`pwd`, `otp`, `recovery`, RFC 8176), and `bifrost:mfa-pending` on an enrollment-only session. The session row records the same.

**Replay and storage.** A code is accepted only for a time step later than the last one accepted, with ±1 step of drift. The secret is sealed with field encryption and bound to the user; recovery codes are 80-bit random values stored as salted SHA-256 and spent on use. If `MfaKeyRef` is later removed, an enrolled user's login answers `403` `{"error":"mfa_unavailable"}` rather than skipping the factor.

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
3. **Authorization semantics are unchanged.** Both paths converge on the same `AppIdentity`, and `IdentityContextMapper` projects either one into the **identical** `UserContext` keys — `tenant_id`, `roles`, `id` and `user_id`. The policy engine, `TenantFilterTransformer`, and role-bypass checks read those keys and never see which provider authenticated the request.

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
| User audit key | `AuditMutationTransformer` | configured by `user-audit-key` (use `user_id`) |
| Arbitrary row filters | `AutoFilterTransformer` | configured by `auto-filter` |

### Changing claim keys

The tenant and audit claim keys can be overridden via model metadata. Each one names the user-context key the module reads. It does not rename the keys `IdentityContextMapper` writes: the identity's tenant is always written as `tenant_id`, and its subject as `id` and `user_id`.

```
":root { tenant-context-key: org_id; user-audit-key: user_id; }"
```

This tells the tenant filter transformer to read `org_id` from the user context instead of `tenant_id`. Nothing maps the identity's tenant to `org_id`, so `org_id` must arrive as a claim of that type on the authenticated principal (for example a custom JWT claim). A single-valued claim is read as its value. A caller with no `org_id` claim, including a local-auth or OIDC-mapped user whose tenant landed in `tenant_id`, is refused, and so is a claim with more than one value; the filter never falls back to `tenant_id` and never serves unscoped rows. A client can never supply either key through wire context. The second setting stamps actor columns with `user_id`, the caller's subject as the mapper writes it. Don't name a raw token claim such as `sub` here: under the default JWT inbound claim map `sub` is renamed and absent, and where it is present it is a string array. A write whose `user-audit-key` value is not a single value is refused.

For additional row-level filters, map columns to claims with `auto-filter`:

```
"dbo.orders { auto-filter: organization_id:org_id,region_id:region; }"
```

## Loading grants per request (`IGrantResolver`)

Claims baked into a token live as long as the token does — a JWT issued for weeks still carries the permissions the user had at sign-in. When your capability set lives in the database and a permission change must take effect *when made*, register a grant resolver. It runs **once per request**, for authenticated principals, at the single point every transport assembles the user context, and its result is **unioned** into the `permissions` context key before any policy, tenant, or filter module reads the context.

The HTTP identity step is memoized per DI scope: one per request, per binary WebSocket message, per hub invocation. An app endpoint that calls `GetBifrostUserContextAsync()` and then awaits `IPolicyGateAccessor.GetAsync()` shares one projection, so the resolver still runs once, even when both are awaited concurrently. The binary WebSocket mount projects once per message, in that message's scope, and a hook that awaits `IPolicyGateAccessor` while serving the message shares it. A long-lived request's own HttpContext is never memoized, since it outlives any one request: a WebSocket request, or an endpoint carrying `DisableRequestTimeoutAttribute`, which ASP.NET puts on every SignalR transport route (SSE and long polling included). A SignalR hub calls `GetBifrostUserContextAsync(scope)` with the invocation's `IServiceProvider`, because the connection's `RequestServices` live as long as the connection. Nothing is memoized in the root service provider: `GetBifrostUserContextAsync(app.Services)` projects afresh on every call, and refuses the caller (`BifrostIdentityRejectedException`) when the scoped grant resolver cannot be resolved from the root. An `IPolicyGateAccessor` resolved from the root (captured by a singleton) throws `InvalidOperationException` rather than share one caller's gate with every other.

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

- **A per-request DB read is the expected shape.** Caching (and its revocation policy) is the application's business — if you cache, you decide how stale a grant may be. Every Bifrost transport gate awaits the resolver on its own async identity step (the HTTP mounts per request, pgwire/RESP/LDAP at login, gRPC per call, MCP per tool call), so no thread blocks on the read. Keep it bounded all the same: it runs on every request.
- **Project through `IBifrostAuthContextFactory.CreateUserContextAsync`.** Every Bifrost gate awaits it, and the default factory runs the resolver there. A host-written front door awaits it too, and a host factory that decorates the default one overrides it and awaits the inner factory's `CreateUserContextAsync`. The synchronous `CreateUserContext` cannot run the resolver: with one registered, the default factory's sync call for an authenticated principal throws `InvalidOperationException` instead of blocking or serving the caller without its grants, and every gate maps that throw to a refused identity. A host factory that implements only the sync method keeps working, because the interface's default `CreateUserContextAsync` calls it, but a decorator that only forwards the sync call is refused while a resolver is registered.
- The result is a *union* with the identity's mapped permissions, never a replacement. A `null` result is treated as empty: the identity's own permissions remain.
- **Fail closed.** A resolver that throws empties the permission set for that call (the degraded projection is never memoized, so the next projection in the same request retries the resolver) — the identity's pre-resolver `permissions` are wiped too — and logs a `Warning` naming the identity id. The exception never reaches the caller; where a policy needs the missing grant the answer is a policy deny, not a server error. The exception is that a resolver which throws after the request was cancelled (whatever it throws: SqlClient raises `SqlException`, not `OperationCanceledException`) refuses the caller rather than serving the empty set. Only `permissions` is wiped: token `roles` are claims of the token itself, not resolver-sourced grants, and remain.
- **The resolver sees only the identity's own tenant.** `identity.TenantId` is the identity mapper's `tenant_id`, not the value under your `tenant-context-key`: the resolver runs before any model is resolved. If `tenant-context-key: org_id` is fed by a raw `org_id` claim, the resolver gets `TenantId = null` while the tenant filter still scopes by `org_id`. Per-tenant grants read that claim from `identity.Claims`, which holds the principal's claims as one `string[]` per claim type, keyed case-insensitively: `identity.Claims.TryGetValue("org_id", out var org)`. This works on every transport, including pgwire, RESP and LDAP, where no `IHttpContextAccessor` exists.
- **`identity.Claims` is the raw principal, not the scoped tenant value.** It includes claim types the user context deliberately drops: a raw claim named after a key the identity mapper owns (`tenant_id`, `tenant_ids`, `roles`, `user_id`, `id`, `permissions`) never reaches the user context. The tenant filter reads the user context under your `tenant-context-key`. Under the default `tenant_id` that is the identity mapper's value, the same one `identity.TenantId` carries. For an OIDC login it is what the issuer's claim mapper produced, so `identity.Claims["tenant_id"]` may be missing or hold a different value. Only when `tenant-context-key` names a raw claim the mapper does not own, such as `org_id`, does the filter read that claim, and only as a single value: a multi-valued claim is refused. So key per-tenant grants on `identity.TenantId` under the default key, and on a single-valued `identity.Claims[<tenant-context-key>]` only when that key names a raw claim.
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

With auth disabled, all requests are treated as unauthenticated, and an unauthenticated caller gets an empty user context. No placeholder identity is assigned. Tenant-filtered tables refuse every request, and actor columns (`created-by`, `updated-by`, `deleted-by`, `soft-delete-by`) and the history `actor` are stamped NULL, so a NOT NULL actor column fails the write.

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
