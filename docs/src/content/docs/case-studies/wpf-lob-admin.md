---
title: "Case Study: Web Admin for a WPF LOB App"
description: A simulated walkthrough of putting a browser-based admin site next to a legacy WPF order-management app — without touching the desktop app or the database schema.
---

:::note[Simulated case study]
Meridian Supply Co. is fictional, but every configuration block and component in this walkthrough is real and runnable. The scenario is modeled on the most common BifrostQL deployment: a healthy legacy database that needs a second, web-based front end.
:::

## The situation

Meridian Supply Co. is a regional industrial distributor. Their order-management system is a **WPF desktop app**, roughly ten years old, talking directly to a SQL Server database called `MeridianOps`. It works. Nobody wants to rewrite it.

The problem is everyone who *isn't* on the desktop app:

- The warehouse lead wants to correct inventory counts from a tablet on the floor.
- Accounting wants to fix billing addresses without filing a ticket to the two developers who still know the WPF codebase.
- The operations manager wants to look up orders from home.

The database (simplified):

```
dbo.customers      (customer_id PK, name, billing_address, credit_limit, created_on, updated_on)
dbo.orders         (order_id PK, customer_id FK, status, order_date, total, created_on, updated_on)
dbo.order_lines    (order_line_id PK, order_id FK, product_id FK, qty, unit_price)
dbo.products       (product_id PK, sku, name, list_price, qty_on_hand)
dbo.sync_state     (internal replication bookkeeping — must never appear in any UI)
dbo.price_history  (append-only, written by a nightly job — read-only for humans)
```

**Constraints:**

1. The WPF app keeps working, unchanged. No schema migrations, no new stored procedures.
2. The web admin must not expose `sync_state`, and must not let anyone write to `price_history`.
3. Every edit made through the web must record who made it and when — the desktop app already maintains `created_on`/`updated_on`, and the web side has to keep that contract.
4. There are two developers. The admin site cannot become a third codebase to maintain.

## The plan

| Constraint | BifrostQL feature |
|------------|-------------------|
| No schema changes | [Schema generation](/BifrostQL/concepts/schema-generation/) reads the existing database as-is |
| Hide `sync_state`, protect `price_history` | [Metadata rules](/BifrostQL/reference/configuration/): `visibility: hidden`, `update: none` |
| Audit columns maintained | `populate: created-on` / `updated-on` rules |
| Minimal new code | The [embeddable editor](/BifrostQL/guides/embedded-editor/) generates the whole UI from the schema |
| Who-did-what | [Local-user auth](/BifrostQL/guides/authentication/) against a small new `app_users` table |

The only database change in the entire project is one new table, `app_users`, for web logins — additive, invisible to the WPF app.

## Walkthrough

### 1. Stand up the API next to the database

A new, tiny ASP.NET Core project. This is the entire server:

```bash
dotnet new web -n MeridianAdmin
cd MeridianAdmin
dotnet add package BifrostQL.Server
dotnet add package BifrostQL.SqlServer
```

```csharp
// Program.cs
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

Before writing any configuration, the team pointed the desktop navigator at the database to sanity-check what BifrostQL would generate:

```bash
bifrost serve --connection "Server=ops-sql;Database=MeridianOps;..."
```

That confirmed the FK relationships (`orders → customers`, `order_lines → orders/products`) were detected automatically, so joins and foreign-key navigation would work with no configuration.

### 2. Shape the API with metadata rules

All the constraints from the brief become declarative rules in `appsettings.json` — no code:

```json
{
  "ConnectionStrings": {
    "bifrost": "Server=ops-sql;Database=MeridianOps;User Id=meridian_web;Password=xxx;TrustServerCertificate=True"
  },
  "BifrostQL": {
    "Path": "/graphql",
    "Playground": "/graphiql",
    "Provider": "sqlserver",
    "Metadata": [
      "dbo.sync_state { visibility: hidden; }",

      "dbo.price_history.* { update: none; }",

      "dbo.*.created_on { populate: created-on; update: none; }",
      "dbo.*.updated_on { populate: updated-on; update: none; }",
      "dbo.*.updated_by { populate: updated-by; update: none; }",

      "dbo.customers { label: name; }",
      "dbo.products  { label: name; }",

      ":root { raw-sql: disabled; generic-table: disabled; default-limit: 50; }"
    ]
  }
}
```

Reading it top to bottom:

- **`sync_state` disappears** from the GraphQL schema entirely. It isn't hidden in the UI — it does not exist in the API, so no client can query it.
- **`price_history` becomes read-only.** Every column rejects updates; the nightly job keeps writing to it directly over SQL, unaffected.
- **Audit columns are server-populated.** When an accountant fixes a billing address, BifrostQL stamps `updated_on` (and `updated_by`, a nullable column Meridian added later once they trusted the pattern) from the authenticated identity. The client cannot supply or forge these values — `update: none` makes them read-only for callers. The desktop app's own writes are untouched; it was already setting these columns itself.
- **`label:` rules** tell generated UIs which column names a row — so a foreign-key dropdown for `customer_id` shows customer names, not integers.
- **The `:root` rule turns off the escape hatches.** No raw SQL, no generic table access. This admin gets exactly the shaped API and nothing else.

One note on the connection: the API runs under its own SQL login, `meridian_web`. Even though BifrostQL enforces the rules above, the team also denied that login write access to `price_history` at the database level. Defense in depth costs one `DENY` statement.

### 3. Web logins without touching the ops schema

The single additive migration:

```sql
CREATE TABLE dbo.app_users (
    id            INT IDENTITY PRIMARY KEY,
    email         NVARCHAR(256) NOT NULL UNIQUE,
    password_hash NVARCHAR(500) NOT NULL,
    display_name  NVARCHAR(200) NOT NULL,
    tenant_id     NVARCHAR(50)  NULL,
    roles         NVARCHAR(400) NOT NULL DEFAULT ''
);
```

This matches `AddBifrostLocalAuth`'s defaults exactly, so the registration in step 1 needs no options callback. Logins hit `POST /auth/login` with `{ "login": "...", "password": "..." }`, get a session cookie, and every subsequent GraphQL request carries the resulting identity — which is what feeds the `populate: updated-by` rule.

The WPF app never sees this table. It authenticates the way it always has.

### 4. The admin UI is one component

The front end is a Vite + React project whose meaningful content is about ten lines:

```tsx
import { Editor } from '@standardbeagle/edit-db';
import '@standardbeagle/edit-db/style.css';

export default function Admin() {
  return (
    <div className="h-screen">
      <Editor uri="/graphql" uiPath="/admin" />
    </div>
  );
}
```

The [editor](/BifrostQL/guides/embedded-editor/) introspects the schema at runtime and renders every visible table: data grids with sorting/filtering/pagination, generated edit forms with validation, and foreign-key click-through (an order row links to its customer and its lines, because the FKs said so).

Because the shaping happened server-side in step 2, the editor is automatically correct:

- `sync_state` never renders — it isn't in the schema.
- `price_history` renders as a browsable grid, but its forms reject edits because every column is `update: none`.
- `created_on` / `updated_on` show as read-only values, not editable inputs.

When the warehouse lead asked for company colors, that was a CSS variable block, not a fork:

```css
@layer editdb-theme, app-theme;

@layer app-theme {
  :root {
    --ui-primary: #1a5c3a;            /* Meridian green */
    --ui-primary-foreground: #ffffff;
  }
}
```

### 5. What about the desktop app?

Nothing. That's the point. The WPF app keeps its direct SQL connection and its own writes. Both front ends share one source of truth — the database — and BifrostQL's audit rules keep web-side writes indistinguishable in shape from desktop-side writes (`created_on`/`updated_on` filled in either way).

The one coordination rule the team adopted: **schema changes are still owned by the desktop team**, and the web API picks them up automatically. When a `discontinued` bit was added to `products`, it appeared in the web admin's product form on the next app restart with zero web-side changes.

## What we didn't have to build

- ❌ A REST/CRUD API layer (~40 endpoints for these six tables)
- ❌ Admin screens, forms, and validation for each table
- ❌ DTOs and mappers between the database and the API
- ❌ An audit-column convention re-implemented in a second codebase
- ❌ Any change to the WPF application

What Meridian *did* write: one `Program.cs`, one `appsettings.json`, one ~10-line React component, one additive table.

## Where to go deeper

- [Metadata rule reference](/BifrostQL/reference/configuration/) — every property used above
- [Embeddable Data Editor](/BifrostQL/guides/embedded-editor/) — props, theming, custom fetchers
- [Authentication](/BifrostQL/guides/authentication/) — local auth options, OIDC if you'd rather use Microsoft 365
- Next case study: [Two admin sections — application API vs. raw SQL](/BifrostQL/case-studies/two-tier-admin/)
