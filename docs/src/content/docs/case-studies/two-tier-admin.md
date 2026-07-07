---
title: "Case Study: Two Admin Sections — App API vs. Raw SQL"
description: A simulated walkthrough of one admin portal with two rooms — a curated admin built on the application-shaped GraphQL API for support staff, and a role-gated raw SQL console for on-call engineers.
---

:::note[Simulated case study]
Brightline Bookings is fictional; the configuration, roles, and queries are real. This walkthrough shows the two ends of the BifrostQL spectrum living in one deployment: the **shaped application API** and the **raw SQL escape hatch** — and why you'd want both, fenced differently.
:::

## The situation

Brightline Bookings runs scheduling software for tour operators. One SQL Server database, one BifrostQL endpoint, one React admin portal. But two very different audiences use "admin":

- **Support staff (about a dozen people).** They fix customer bookings all day: reschedule, refund, merge duplicate customers. They should only ever see the world through the application's rules — soft-deleted rows stay hidden, cancelled bookings can't be un-cancelled by editing a status string, and every change is attributed.
- **On-call engineers (two people).** At 2 a.m., when a support ticket says "the totals on invoice 8841 don't add up," they need to *investigate* — join across tables in ways no one anticipated, check for orphaned rows, compare against the audit trail. They need SQL, and they need it read-only.

Historically the engineers used SSMS against production with a shared password. Nobody was happy about that: no attribution, no row limits, no timeouts, and a copy of the production credentials on every laptop.

**Constraints:**

1. One portal, one login system. Which sections you see depends on your roles.
2. Support staff can never reach raw SQL — not hidden-by-CSS, but structurally unable.
3. The SQL console is SELECT-only, capped, time-boxed, and attributed.
4. Support-side mutations must respect soft delete and audit columns even when a support agent edits "directly."

## The plan

| Constraint | BifrostQL feature |
|------------|-------------------|
| One login, role-based sections | [Local auth](/BifrostQL/guides/authentication/) `roles` column → `AppIdentity.Roles` |
| Curated admin | Shaped GraphQL API + [embeddable editor](/BifrostQL/guides/embedded-editor/) |
| Soft delete & audit enforced server-side | `delete-type: soft`, `populate:` [metadata rules](/BifrostQL/reference/configuration/) |
| Raw SQL, fenced | `_rawQuery` with `raw-sql-role`, `raw-sql-timeout`, `raw-sql-max-rows` |

The key insight: **these aren't two permission levels on one surface — they're two different surfaces**, and the server decides who gets which. The raw SQL field simply rejects callers without the role, so the support build of the UI has nothing to hide.

## Walkthrough

### 1. One server, both surfaces

```json
{
  "ConnectionStrings": {
    "bifrost": "Server=db;Database=Brightline;User Id=brightline_api;Password=xxx;TrustServerCertificate=True"
  },
  "BifrostQL": {
    "Path": "/graphql",
    "Playground": "/graphiql",
    "Provider": "sqlserver",
    "Metadata": [
      "dbo.bookings  { delete-type: soft; soft-delete: deleted_at; soft-delete-by: deleted_by; }",
      "dbo.customers { delete-type: soft; soft-delete: deleted_at; soft-delete-by: deleted_by; }",

      "dbo.*.created_on { populate: created-on; update: none; }",
      "dbo.*.created_by { populate: created-by; update: none; }",
      "dbo.*.updated_on { populate: updated-on; update: none; }",
      "dbo.*.updated_by { populate: updated-by; update: none; }",

      "dbo.customers { label: full_name; }",
      "dbo.tours     { label: title; }",

      ":root { raw-sql: enabled; raw-sql-role: oncall-engineer; raw-sql-timeout: 30; raw-sql-max-rows: 5000; generic-table: disabled; default-limit: 50; }"
    ]
  }
}
```

The last rule is the whole ops story in one line:

- `raw-sql: enabled` exposes a single extra query field, `_rawQuery(sql: String!, params: JSON, timeout: Int): [JSON]!`.
- `raw-sql-role: oncall-engineer` — callers without that role get an authorization error, regardless of what UI they're using. (If you don't set it, the default required role is `bifrost-raw-sql`.)
- `raw-sql-timeout: 30` and `raw-sql-max-rows: 5000` bound the blast radius of any query. No more accidental table-scan-at-2-a.m. holding locks for ten minutes.
- Raw SQL is **SELECT-only** — the server validates statements and rejects anything that writes. The engineers investigate through SQL; they *fix* through the application API like everyone else, which keeps every write inside the audit and soft-delete rules.

`app_users.roles` holds a delimited list (e.g. `support` or `support,oncall-engineer`), which local auth parses into `AppIdentity.Roles`. No separate role system to run.

### 2. The support admin: application API only

The support section is the [embeddable editor](/BifrostQL/guides/embedded-editor/) plus a handful of bespoke screens. Because the shaping is server-side, the notable behaviors come for free:

- **"Delete" is soft.** When a support agent deletes a duplicate customer, the server sets `deleted_at`/`deleted_by` instead of removing the row — and every subsequent query silently excludes it. The agent doesn't know or care.
- **Attribution can't be skipped.** `created_by`/`updated_by` are populated from the session identity and are `update: none`, so no request payload can spoof them.
- **The status quo is queryable.** A bespoke "today's refunds" screen is just a filtered query against the same endpoint:

```graphql
{
  bookings(
    filter: { status: { _eq: "refunded" }, updated_on: { _gt: "2026-07-06" } }
    sort: [updated_on_desc]
    limit: 50
  ) {
    data {
      bookingId
      status
      total
      updated_by
      customers { fullName }
    }
  }
}
```

Support users *can* open the GraphQL endpoint directly with their own session — and that's fine. The API **is** the security boundary. Anything they can do with cURL is exactly what they can do in the UI, because the rules live in the metadata, not the front end.

### 3. The ops console: `_rawQuery` behind a role

The ops section is one page: a textarea, a run button, and a results grid. The GraphQL call:

```graphql
query Investigate($sql: String!, $params: JSON) {
  _rawQuery(sql: $sql, params: $params, timeout: 20)
}
```

```json
{
  "sql": "SELECT b.booking_id, b.total, SUM(bl.qty * bl.unit_price) AS line_total FROM bookings b JOIN booking_lines bl ON bl.booking_id = b.booking_id WHERE b.invoice_no = @invoice GROUP BY b.booking_id, b.total HAVING b.total <> SUM(bl.qty * bl.unit_price)",
  "params": { "invoice": "8841" }
}
```

`_rawQuery` returns a list of name-keyed JSON rows, so the console renders whatever comes back without knowing the shape in advance. Parameters go through `params` — the console never string-interpolates user input into SQL.

The React side is deliberately boring — a fetch with credentials and a table:

```tsx
async function runSql(sql: string, params: Record<string, unknown>) {
  const res = await fetch('/graphql', {
    method: 'POST',
    credentials: 'include',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      query: 'query($sql: String!, $params: JSON) { _rawQuery(sql: $sql, params: $params, timeout: 20) }',
      variables: { sql, params },
    }),
  });
  const { data, errors } = await res.json();
  if (errors) throw new Error(errors[0].message);
  return data._rawQuery as Record<string, unknown>[];
}
```

What happens when a support agent somehow reaches this page (bookmark, mistyped role check, malice)? The server rejects the call — the `oncall-engineer` role check happens in the resolver, not the router. The front-end role check that hides the nav item is a courtesy, not a control.

### 4. Routing the two sections

```tsx
import { Editor } from '@standardbeagle/edit-db';
import '@standardbeagle/edit-db/style.css';

export default function AdminPortal({ user }: { user: Session }) {
  return (
    <Routes>
      <Route path="/admin/*" element={<Editor uri="/graphql" uiPath="/admin" />} />
      {user.roles.includes('oncall-engineer') && (
        <Route path="/ops" element={<SqlConsole />} />
      )}
    </Routes>
  );
}
```

One portal, one session cookie, one GraphQL endpoint. The difference between the rooms is a role the server checks.

### 5. What replaced SSMS-with-a-shared-password

| Before | After |
|--------|-------|
| Shared production credentials on laptops | Individual logins; DB credentials only on the server |
| No attribution | Every request tied to an `AppIdentity` |
| Unbounded queries | 30 s timeout, 5,000-row cap |
| Accidental writes possible | SELECT-only validation |
| All-or-nothing access | Role on the user row |

And notably: the engineers stopped resenting the guardrails, because investigation queries are 99% of what they did in SSMS anyway. For the 1% (an actual data fix), the shaped API's mutations — with audit trail — are the *better* tool.

## What we didn't have to build

- ❌ A second "admin API" for support (the shaped GraphQL API is it)
- ❌ A query-runner service with its own auth, limits, and audit
- ❌ Client-side enforcement of soft delete / audit rules (server metadata owns them)
- ❌ A role system (one delimited column on `app_users`)

## Where to go deeper

- [Configuration reference](/BifrostQL/reference/configuration/) — all `raw-sql-*` and soft-delete properties
- [Mutations](/BifrostQL/guides/mutations/) — the write path support staff use
- [Module System](/BifrostQL/guides/modules/) — how the soft-delete and audit transformers compose
- Next case study: [Multi-tenant field-service SaaS](/BifrostQL/case-studies/multi-tenant-saas/)
