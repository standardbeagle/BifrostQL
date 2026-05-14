# HostedSpa Sample

A minimal [Vite](https://vitejs.dev/) single-page application and a BifrostQL GraphQL
API served from **one ASP.NET process**. The SPA calls a same-origin `/graphql`
endpoint, so there is no CORS configuration to manage.

## What it shows

- `UseBifrostQL()` exposes the GraphQL endpoint at `/graphql` and a GraphiQL
  playground at `/playground`.
- `UseBifrostSpa()` serves the built SPA assets and adds an `index.html` route
  fallback that does not shadow `/graphql`, `/playground`, `/api`, or `/health`.
- A small SQLite database (`hostedspa-sample.db`) is created and seeded on first
  run, so no external database setup is required.

## Running it

The SPA is committed pre-built under `wwwroot/`, so the sample runs as-is:

```bash
dotnet run --project samples/HostedSpa
```

Then open the printed URL (for example `http://localhost:5000`):

- `/` — the SPA. Click **Load widgets from /graphql** to query the API.
- `/playground` — the GraphiQL playground.
- `/graphql` — the GraphQL endpoint (POST).

## Identity-to-member linking

The Membership Manager schema has two related identity tables: `app_users` holds
login accounts, and `members` holds membership profiles. `members.user_id` is the
FK that links a profile to its login — some members are login accounts, others
(children, prospective members) are not, so `members.user_id` is nullable.

Linking is **explicit and auditable**. It is not a side effect of any CRUD write:
`audit_log` is read-only through the generated GraphQL API, and `members.user_id`
is set only through the sidecar endpoint below.

- **Endpoint** — `POST /workflows/membership/link-identity` with body
  `{ "memberId": <id>, "userId": <id> }`. It is one of the
  `MembershipWorkflowEndpoints` sidecar operations: it orchestrates through
  `IBifrostWorkflowExecutor`, so the `members` update traverses the same GraphQL
  mutation pipeline (and policy engine) as a direct `/graphql` request.
- **Who can link** — the endpoint is policy-gated: a pre-flight
  `PolicyEvaluator` check on the `members` table's `Update` action rejects the
  whole operation up front. Both the member and the `app_users` row must also be
  visible to the caller (tenant-scoped reads) before any write.
- **What is recorded** — on success the endpoint sets `members.user_id` and
  appends exactly one `audit_log` row: `action = member.identity-linked`,
  `actor_user_id` = the authenticated caller, `entity_type = member`,
  `entity_id` = the member id, and a human-readable `summary`. A rejected link
  (unknown member or unknown user) writes nothing.

### Household claim enrichment

The Membership Manager policy epic scopes `households` rows by
`household_id = {household_id}`, so an authenticated caller's user context must
carry a `household_id` claim resolved from their own member row. This is opt-in
on `AddBifrostLocalAuth` via the `MemberTable`, `MemberUserIdColumn`, and
`MemberHouseholdColumn` options: when all three are set, a successful login
resolves the caller's member row and surfaces its `household_id` as an
`AppIdentity` provider claim. The claim is carried through the auth cookie and
projected into the user context by `IdentityContextMapper`, so both the
`members` row-scope (`user_id = {user_id}`) and the `households` row-scope
(`household_id = {household_id}`) resolve end to end. A member with no household
simply carries no `household_id` claim.

## Rebuilding the SPA

The SPA source lives under `spa/`. To change it and rebuild into `wwwroot/`:

```bash
cd samples/HostedSpa/spa
npm install
npm run build
```

For a SPA dev-server workflow with hot reload, run the ASP.NET host (`dotnet run`)
and `npm run dev` together — the Vite dev server proxies `/graphql` to the host.

## Project layout

| Path | Purpose |
|------|---------|
| `Program.cs` | ASP.NET host wiring `UseBifrostQL` + `UseBifrostSpa`. |
| `SampleDatabase.cs` | Creates and seeds the SQLite sample database on first run. |
| `appsettings.json` | BifrostQL configuration (SQLite provider, endpoint paths). |
| `spa/` | Vite SPA source. |
| `wwwroot/` | Pre-built SPA assets served by the host. |
