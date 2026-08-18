# BifrostQL docs — keyword map

Status: PHASE 0 OUTPUT (2026-08-17). Input to Phase 1 (`seo-optimizer` per-page
pass) of `content-marketing-plan-2026-08.md`. Covers all **80** pages under
`docs/src/content/docs`.

This file assigns exactly one **primary keyword** per page. No two pages share a
primary — that is the anti-cannibalization contract. Where a concept page and a
guide page cover the same feature, the concept page owns the *what/why* phrase
and the guide page owns the *how/task* phrase.

## Sitewide conventions

- **Title**: 50–60 characters, **keyword-first**. The primary keyword (or its
  head noun phrase) leads the title; brand/qualifier follows. Starlight renders
  `<title>` as `<page title> | BifrostQL`, so the frontmatter `title` should
  stay at or under ~48 chars to leave room for the site suffix within the
  60-char SERP budget. Current state: **77 of 80 titles are under 50 chars**
  (most are bare nouns like `Joins`, `Queries`, `Mutations`), 2 in band, 1 over.
- **Description**: 150–160 characters, one sentence, contains the primary
  keyword, states the payoff not the topic. Current state: **2 pages have no
  description at all**, 22 are under 150, only 4 land in band, and **52 are over
  170** — several run past 400 chars and will be truncated in every SERP.
- **URL / slug**: **frozen**. No page is renamed or moved in Phase 1. The site
  is served under `base: '/BifrostQL'` at `https://dev.standardbeagle.com`;
  changing a slug costs a redirect the static host does not currently provide.
- **One H1 per page** (Starlight derives it from `title` — do not add a second
  `#` heading in the body).
- Primary keyword should appear in the first 100 words of body copy.
- Secondary keywords are for H2s, internal-link anchor text, and the body — not
  for stuffing the description.

### Anchor terms and where they land

The plan's high-value anchor terms are each owned by exactly one page:

| Anchor term | Owning page |
|---|---|
| GraphQL API for your existing SQL database | `index.mdx` |
| database to GraphQL API (SQL Server, Postgres, MySQL, SQLite) | `getting-started/index` |
| no-code GraphQL backend / zero-code admin | `guides/embedded-editor` |
| Postgres wire protocol emulation | `guides/pgwire` |
| database MCP server | `guides/mcp-server` |
| LDAP server from a database | `guides/ldap` |
| headless CRUD admin UI | `guides/workbench/index` |
| GraphQL N+1 problem solution | `concepts/n-plus-one` |
| cross-database SQL dialect support | `reference/dialects` |

---

## Landing + getting started

| Page | Primary keyword | Secondary (2–3) | Title | Desc |
|---|---|---|---|---|
| `index.mdx` | GraphQL API for your existing SQL database | zero-code GraphQL server; SQL Server Postgres MySQL SQLite GraphQL; instant GraphQL backend | yes (7 ch — far too short) | yes (48 ch — too short) |
| `getting-started/index.md` | database to GraphQL API in five minutes | install BifrostQL; GraphQL from a connection string; first GraphQL query | yes (15 ch) | yes (68 ch) |
| `getting-started/examples.md` | BifrostQL example projects | schema-driven HTML forms sample; embeddable React editor demo; minimal host app | yes (16 ch) | yes (128 ch) |

## Core concepts

| Page | Primary keyword | Secondary (2–3) | Title | Desc |
|---|---|---|---|---|
| `concepts/schema-generation.md` | automatic GraphQL schema from a database | database introspection to GraphQL types; generated query and mutation fields; schema caching | yes (17 ch) | yes (55 ch) |
| `concepts/n-plus-one.md` | GraphQL N+1 query problem solution | batched SQL per table; one database round-trip; DataLoader alternative | yes (37 ch) | yes (162 ch — in band) |
| `concepts/computed-columns-and-validation.md` | SQL computed columns in GraphQL | virtual fields; .NET provider-backed computed fields; server-side validation rules | yes (43 ch) | yes (117 ch) |
| `concepts/lookup-table-enums.md` | lookup table to GraphQL enum | typed enum columns; filterable enum values; enum metadata | yes (20 ch) | yes (156 ch — in band) |
| `concepts/pivot.md` | server-side pivot and cross-tab queries | GraphQL cross-tab field; SQL PIVOT across dialects; distinct-value cardinality cap | yes (25 ch) | yes (~190 ch) |
| `concepts/saved-objects.md` | saved queries forms reports dashboards store | saved-object CRUD endpoint; user-authored object persistence; saved-object vs schema metadata | yes (46 ch) | yes (~230 ch) |
| `concepts/eav-meta.md` | entity-attribute-value tables as GraphQL JSON | wp_postmeta GraphQL; EAV side-table flattening; `_meta` field | yes (32 ch) | yes (~215 ch) |
| `concepts/app-schema-detection.md` | automatic application schema detection | WordPress schema auto-config; implicit foreign key injection; internal table hiding | yes (28 ch) | yes (~185 ch) |
| `concepts/app-metadata-overlay.md` | app metadata overlay for client UIs | labels forms grids JSON layer; SPA presentation metadata; overlay vs schema metadata | yes (21 ch) | yes (~245 ch) |
| `concepts/protocol-adapters.md` | one pipeline many database front doors | protocol adapter architecture; intent executor seam; unconditional tenant and policy guards | yes (48 ch) | yes (~250 ch) |
| `concepts/grpc-schema-contract.md` | stable gRPC wire contract from a database | field-number manifest; dynamic proto descriptor; schema drift stability | yes (58 ch — in band) | yes (~300 ch) |
| `concepts/cdc-outbound-events.md` | change data capture from SQL tables | transactional outbox pattern; webhook and queue delivery; insert update delete events | yes (36 ch) | yes (~205 ch) |
| `concepts/field-encryption.md` | database column encryption at rest | AES-256-GCM envelope encryption; blind index search on encrypted columns; per-role masking | yes (33 ch) | yes (~245 ch) |
| `concepts/temporal-history.md` | field-level change history for SQL tables | before/after audit trail; who changed what and when; atomic history write | yes (24 ch) | yes (~185 ch) |
| `concepts/chat.md` | LLM chat over your own database tables | conversation and message schema; tenant-isolated chat; encrypted chat fields | yes (21 ch) | yes (~185 ch) |
| `concepts/visual-query-builder.md` | Access-style visual query builder | drag-and-drop multi-table SELECT; joins and criteria designer; parameterized SQL generation | yes (21 ch) | yes (~280 ch) |

## Guides — query and write surface

| Page | Primary keyword | Secondary (2–3) | Title | Desc |
|---|---|---|---|---|
| `guides/queries.md` | GraphQL filtering sorting and pagination | filter operator reference; offset and cursor paging; sort by multiple columns | yes (7 ch) | yes (52 ch) |
| `guides/aggregate-queries.md` | GraphQL GROUP BY aggregate queries | count sum avg min max over SQL; server-side aggregation; aggregate field | yes (30 ch) | yes (~195 ch) |
| `guides/joins.md` | automatic table joins in GraphQL | foreign-key relationship traversal; explicit join configuration; many-to-many links | yes (5 ch) | yes (44 ch) |
| `guides/full-text-search.md` | cross-database full-text search in GraphQL | `_search` filter operator; SQL Server catalog Postgres GIN MySQL FULLTEXT SQLite FTS5; phrase and multi-term semantics | yes (26 ch) | yes (~400 ch) |
| `guides/mutations.md` | GraphQL insert update upsert delete | batch mutations; nested TreeSync writes; upsert semantics | yes (9 ch) | yes (55 ch) |
| `guides/expression-builder.md` | portable SQL expression builder | SqlExprBuilder fluent API; cross-dialect expression lowering; build-time expression validation | yes (33 ch) | yes (~250 ch) |

## Guides — security, tenancy, lifecycle

| Page | Primary keyword | Secondary (2–3) | Title | Desc |
|---|---|---|---|---|
| `guides/authentication.md` | GraphQL API authentication and OIDC | JWT bearer tokens; local user login; shared identity contract | yes (14 ch) | yes (86 ch) |
| `guides/org-model.md` | multi-tenant organization data model | tenant isolation metadata; tenant-owned mutations; canonical tenant claims | yes (20 ch) | yes (~170 ch) |
| `guides/field-encryption.md` | rotating field-encryption keys | DEK version in ciphertext; online re-encryption sweep; root key vs data key rotation | yes (30 ch) | yes (~370 ch) |
| `guides/retention.md` | data retention and right to erasure | GDPR erasure for SQL rows; retain vs TTL purge policy; change-history tombstoning | yes (34 ch) | yes (~350 ch) |
| `guides/change-history.md` | recording row change history | before/after field trail; batch and nested write history; same-transaction audit | yes (25 ch) | yes (~200 ch) |
| `guides/cdc-events.md` | emitting change events from tables | transactional outbox configuration; CDC across batch writes; event metadata | yes (28 ch) | yes (~150 ch — in band) |
| `guides/state-machines.md` | metadata-defined state machines | lifecycle transition enforcement; allowed status transitions; state guard rules | yes (14 ch) | yes (63 ch) |
| `guides/workflows.md` | metadata-driven database workflows | workflow definitions; mutation pipeline workflows; workflow execution | yes (9 ch) | yes (72 ch) |
| `guides/workflow-mutations.md` | workflow mutations and audit trail | sidecar workflow endpoints; policy-engine gated operations; tenant-scoped audit log | yes (33 ch) | yes (~180 ch) |
| `guides/approval-workflows.md` | maker-checker approval for row edits | pending change interception; approval replay through the pipeline; approval expiry | yes (19 ch) | **NO — missing** |
| `guides/deferred-effects.md` | deferred and reversible change sets | undo a committed write; transactional reverse deltas; change-set review queue | yes (17 ch) | **NO — missing** |

## Guides — protocol adapters

| Page | Primary keyword | Secondary (2–3) | Title | Desc |
|---|---|---|---|---|
| `guides/protocol-adapters.md` | authoring a custom protocol adapter | IProtocolAdapter contract; intent executor seam; adapter conformance kit | yes (30 ch) | yes (~275 ch) |
| `guides/pgwire.md` | Postgres wire protocol emulation | connect psql and JDBC to any database; Grafana and Metabase over pgwire; read-only SQL subset | yes (37 ch) | yes (~250 ch) |
| `guides/pgwire-bi-smoke.md` | pgwire BI tool smoke runbook | Grafana Metabase psql verification; real socket connection test; manual adapter runbook | yes (33 ch) | yes (~230 ch) |
| `guides/resp.md` | Redis wire protocol for SQL rows | redis-cli against a database; key-addressed row reads; StackExchange.Redis compatibility | yes (30 ch) | yes (~280 ch) |
| `guides/resp-smoke.md` | RESP smoke runbook | redis-cli verification; StackExchange.Redis end-to-end; tenant-scoped read check | yes (31 ch) | yes (~250 ch) |
| `guides/ldap.md` | LDAP directory server from a database | publish users and groups as LDAP entries; LDAPS and StartTLS; ldapsearch and Grafana LDAP login | yes (26 ch) | yes (~430 ch) |
| `guides/ldap-smoke.md` | LDAP smoke runbook | ldapsearch verification; Grafana LDAP login test; bind over real socket | yes (20 ch) | yes (~290 ch) |
| `guides/odata.md` | OData v4 endpoint for SQL tables | Excel and Power BI database connection; `$filter` `$expand` query options; server-driven paging | yes (20 ch) | yes (~400 ch) |
| `guides/grpc.md` | gRPC endpoint over your database | server reflection without .proto files; grpcurl workflow; proto3 type mapping | yes (14 ch) | yes (~460 ch) |
| `guides/s3.md` | S3-compatible object storage over SQL | AWS CLI and rclone against a database; file column blobs as objects; SigV4 and presigned GET | yes (33 ch) | yes (~330 ch) |
| `guides/prometheus.md` | Prometheus metrics from database tables | per-table business metrics; scrape credential and tenant scoping; engine self-metrics | yes (28 ch) | yes (~430 ch) |
| `guides/feeds.md` | RSS and Atom feeds from SQL tables | syndication feed metadata; conditional GET caching; revocable feed tokens | yes (32 ch) | yes (~370 ch) |
| `guides/binary-transport.md` | protobuf over WebSocket transport | chunked result streaming; automatic resume across disconnects; TypeScript codegen from .proto | yes (24 ch) | yes (~185 ch) |

## Guides — AI and chat

| Page | Primary keyword | Secondary (2–3) | Title | Desc |
|---|---|---|---|---|
| `guides/mcp-server.md` | database MCP server for AI agents | Model Context Protocol over SQL; Claude Code database tools; stdio and HTTP bearer hosting | yes (44 ch) | yes (~460 ch) |
| `guides/mcp-tool-authoring.md` | designing MCP tools for a database | tool budget and consolidation; includes and folded reads; off-by-default write tools | yes (21 ch) | yes (~250 ch) |
| `guides/llm-chat.md` | streaming LLM chat endpoints | server-sent events chat; fail-closed chat tenancy; typed terminal contracts | yes (19 ch) | yes (~105 ch) |
| `guides/chat-connectors.md` | chat connectors for your tables | tables as Claude tools; human-gated plan writes; custom connector registration | yes (16 ch) | yes (~185 ch) |

## Guides — clients and hosting

| Page | Primary keyword | Secondary (2–3) | Title | Desc |
|---|---|---|---|---|
| `guides/react-hooks.md` | React hooks for a GraphQL database API | typed query and mutation hooks; infinite scroll and subscriptions; TanStack Query headless table | yes (26 ch) | yes (~190 ch) |
| `guides/embedded-editor.md` | embeddable no-code CRUD editor | drop-in React database editor; automatic schema-driven forms; CSS variable theming | yes (24 ch) | yes (~230 ch) |
| `guides/react-native.md` | React Native client support | mobile GraphQL database client; auth refresh and session failure; known React Native gaps | yes (12 ch) | yes (~120 ch) |
| `guides/hosted-spa.md` | hosting an SPA and GraphQL API together | single ASP.NET process; local dev proxy; production static hosting | yes (23 ch) | yes (~95 ch) |
| `guides/desktop-app.md` | desktop database explorer app | native GraphQL playground; Photino desktop shell; local database browsing | yes (26 ch) | yes (63 ch) |
| `guides/wordpress.md` | GraphQL API for a WordPress database | WordPress MySQL introspection; postmeta flattening; PHP serialized value decoding | yes (26 ch) | yes (~145 ch) |

## Guides — extensibility and internals

| Page | Primary keyword | Secondary (2–3) | Title | Desc |
|---|---|---|---|---|
| `guides/modules.md` | cross-cutting modules and transformers | filter transformers; mutation transformers; query observers | yes (13 ch) | yes (~95 ch) |
| `guides/extensibility.md` | extending the pipeline with C# hooks | before-commit veto hooks; async server validation; provider-backed computed columns | yes (35 ch) | yes (~250 ch) |
| `guides/developer-guide.md` | contributing to BifrostQL | debugging and logging; local development workflow; build and test loop | yes (15 ch) | yes (~100 ch) |

## Data workbench

| Page | Primary keyword | Secondary (2–3) | Title | Desc |
|---|---|---|---|---|
| `guides/workbench/index.md` | headless CRUD admin over your database | Access-style data workbench; SQL editor and ER diagram; charts pivots and dashboards | yes (24 ch) | yes (~250 ch) |
| `guides/workbench/saved-queries.md` | saving and reusing database queries | visual query design persistence; schema-drift handling; reopen and run a saved query | yes (13 ch) | yes (~185 ch) |
| `guides/workbench/forms.md` | database forms and subforms | record navigation and CRUD; foreign-key-bound subforms; composite-key-safe forms | yes (18 ch) | yes (~180 ch) |
| `guides/workbench/printable-tables.md` | printable tabular reports | group bands and subtotals; server-computed totals; report CSV export | yes (16 ch) | yes (~245 ch) |
| `guides/workbench/sql-editor.md` | schema-aware SQL editor | dialect-aware syntax highlighting; schema autocomplete; desktop-only SQL execution | yes (10 ch) | yes (~215 ch) |
| `guides/workbench/erd.md` | automatic ER diagram from a database | entity relationship visualization; foreign key and many-to-many edges; polymorphic relationships | yes (11 ch) | yes (~250 ch) |
| `guides/workbench/export.md` | CSV and JSON data export | RFC 4180 quoting; full result-set export; BigInt-safe JSON and Excel BOM | yes (17 ch) | yes (~215 ch) |
| `guides/workbench/charts.md` | charts from SQL aggregate queries | bar line pie area panels; server GROUP BY backed charts; high-cardinality guard | yes (11 ch) | yes (~245 ch) |
| `guides/workbench/pivot-ui.md` | drag-and-drop pivot table designer | rows columns values field wells; debounced pivot re-query; saved-query pivot source | yes (8 ch) | yes (~215 ch) |
| `guides/workbench/dashboards.md` | building database dashboards | chart count-card and table tiles; independent tile fetching; edit vs view mode | yes (10 ch) | yes (~230 ch) |
| `guides/workbench/grouping.md` | grouping rows in the data grid | server-computed group counts; group totals not client sums; URL-persisted grouping | yes (13 ch) | yes (~200 ch) |

## Case studies

| Page | Primary keyword | Secondary (2–3) | Title | Desc |
|---|---|---|---|---|
| `case-studies/index.md` | BifrostQL deployment patterns | real-world architecture shapes; case study index; deployment topology examples | yes (21 ch) | yes (~165 ch — in band) |
| `case-studies/wpf-lob-admin.md` | web admin for a legacy WPF app | modernizing a desktop LOB app; browser admin without schema change; legacy database front end | yes (43 ch) | yes (~185 ch) |
| `case-studies/two-tier-admin.md` | curated admin plus raw SQL console | role-gated SQL access; two-tier admin portal; support staff vs on-call engineer views | yes (49 ch — in band) | yes (~250 ch) |
| `case-studies/multi-tenant-saas.md` | multi-tenant SaaS back office | server-enforced tenant isolation; soft delete and audit; client code without tenant IDs | yes (43 ch) | yes (~205 ch) |

## Reference

| Page | Primary keyword | Secondary (2–3) | Title | Desc |
|---|---|---|---|---|
| `reference/configuration.md` | BifrostQL configuration reference | appsettings options; connection string configuration; feature toggle settings | yes (13 ch) | yes (48 ch) |
| `reference/dialects.md` | cross-database SQL dialect support | SQL Server Postgres MySQL SQLite differences; dialect capability matrix; identifier quoting | yes (12 ch) | yes (76 ch) |
| `reference/mcp-declarative-tools.md` | declarative MCP tool document reference | MCP tool DSL keys; root and byId reads with includes; per-tool policy and mutation tools | yes (30 ch) | yes (~300 ch) |

---

## Cannibalization resolutions

Four feature areas have both a concept page and a guide page. Each pair is split
by intent so neither competes for the other's query:

| Feature | Concept page owns (what/why) | Guide page owns (how/task) |
|---|---|---|
| Field encryption | `concepts/field-encryption` — *database column encryption at rest* | `guides/field-encryption` — *rotating field-encryption keys* |
| Change history | `concepts/temporal-history` — *field-level change history for SQL tables* | `guides/change-history` — *recording row change history* |
| CDC | `concepts/cdc-outbound-events` — *change data capture from SQL tables* | `guides/cdc-events` — *emitting change events from tables* |
| Protocol adapters | `concepts/protocol-adapters` — *one pipeline many database front doors* | `guides/protocol-adapters` — *authoring a custom protocol adapter* |
| Pivot | `concepts/pivot` — *server-side pivot and cross-tab queries* | `guides/workbench/pivot-ui` — *drag-and-drop pivot table designer* |
| Chat | `concepts/chat` — *LLM chat over your own database tables* | `guides/llm-chat` — *streaming LLM chat endpoints*; `guides/chat-connectors` — *chat connectors for your tables* |
| gRPC | `concepts/grpc-schema-contract` — *stable gRPC wire contract from a database* | `guides/grpc` — *gRPC endpoint over your database* |

Each adapter's **smoke runbook** (`pgwire-bi-smoke`, `resp-smoke`, `ldap-smoke`)
is deliberately given a low-volume operational primary (`… smoke runbook`) so it
never competes with its parent adapter guide. Runbooks should carry an explicit
canonical-style internal link up to the parent guide, and the parent guide should
be the only one of the pair targeting the tool names (psql, Grafana, redis-cli,
ldapsearch) in its title.

The three pages **not in the sidebar** (`guides/approval-workflows`,
`guides/deferred-effects`, `guides/developer-guide`) are orphans — reachable only
by URL, so they accrue no internal link equity. Adding them to `astro.config.mjs`
is a Phase 1 prerequisite, not an optional polish.

## New pages this map anticipates

These do not exist yet (see `feature-matrix.md`). Their primaries are reserved
here so Phase 2 articles do not collide with existing pages:

| Planned page | Reserved primary keyword |
|---|---|
| `getting-started/connect-a-database` | SQL Server Postgres MySQL SQLite connection strings |
| `getting-started/app-schemas/index` | ready-made database schemas |
| `getting-started/app-schemas/blog` | headless blog database schema |
| `getting-started/app-schemas/crm` | CRM database schema with admin UI |
| `getting-started/app-schemas/ecommerce` | ecommerce database schema |
| `getting-started/app-schemas/project-tracker` | project tracker database schema |
| `getting-started/app-schemas/classroom` | classroom or LMS database schema |
| `getting-started/app-schemas/membership-manager` | membership management database schema |
| `getting-started/app-schemas/org-model` | organization and tenancy database schema |
| `getting-started/app-schemas/sqlite-advanced` | advanced SQLite feature schema |
