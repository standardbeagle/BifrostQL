---
title: Authentication
description: JWT bearer token setup and user context in BifrostQL.
---

BifrostQL supports OAuth2/OIDC via JWT bearer tokens. When authentication is enabled, the user's identity drives tenant isolation, audit column population, and any custom modules that depend on user context.

## Setup

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

BifrostQL builds a `BifrostContext` from the authenticated user's JWT claims. This context is available to all modules and transformers.

The context exposes a `UserContext` dictionary keyed by claim names. Modules read from this dictionary to populate filters and audit columns.

### Key claims

| Claim | Used by | Default key |
|-------|---------|-------------|
| Tenant ID | `TenantFilterTransformer` | `tenant_id` |
| User audit key | `BasicAuditModule` | `user-audit-key` |

### Changing claim keys

The default claim keys can be overridden via metadata:

```
"dbo.* { tenant-context-key: org_id; }"
```

This tells the tenant filter transformer to read `org_id` from the user context instead of `tenant_id`.

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
