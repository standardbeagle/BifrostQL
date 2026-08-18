# BifrostQL feature → docs matrix

Status: PHASE 0 OUTPUT (2026-08-17). Input to Phases 1–2 of
`content-marketing-plan-2026-08.md`. Companion file: `keyword-map.md`.

Method: feature census from `git log origin/main` (the 154-commit push, back
through the retention/approval/deferred/feeds/crypto/LDAP epics), `AGENTS.md`,
and the `src/` tree; docs census from the **80** pages under
`docs/src/content/docs`. Staleness judged at title/section level by reading the
shipping source, per the docs-authority rule — not line by line.

Status values:

- **covered** — a page exists and its sections match shipped behavior.
- **stale** — a page exists but contradicts or lags shipped behavior.
- **missing** — the feature ships and no page documents it.

---

## Headline counts

| | Count |
|---|---|
| Docs pages | 80 |
| Feature areas assessed | 48 |
| covered | 33 |
| stale | 7 |
| missing | 8 |
| Pages with no `description` frontmatter | 2 |
| Pages orphaned from the sidebar | 3 |

---

## 1. Query and write surface

| Feature area | Page(s) | Status |
|---|---|---|
| Schema generation from a database | `concepts/schema-generation` | covered |
| N+1 elimination / batched SQL | `concepts/n-plus-one` | covered |
| Queries — filter, sort, paginate | `guides/queries` | covered |
| Mutations — insert/update/upsert/delete, TreeSync | `guides/mutations` | covered |
| Joins and relationship traversal | `guides/joins`, `concepts/schema-generation` | covered |
| Aggregate queries (GROUP BY) | `guides/aggregate-queries` | covered |
| Pivot / cross-tab | `concepts/pivot` | covered (corrected by 1ecce6cb) |
| EAV `_meta` field | `concepts/eav-meta` | covered |
| Lookup-table enums | `concepts/lookup-table-enums` | covered |
| Full-text search (`_search`) | `guides/full-text-search` | covered |
| **Computed columns — new `Expression` kind** | `concepts/computed-columns-and-validation` | **stale** |
| SqlExpr public builder | `guides/expression-builder` | covered |
| SQL dialects | `reference/dialects` | covered |

**Fix note — `concepts/computed-columns-and-validation`**: last touched
2026-06-27, predating commits `50ae8136` / `2d5292c5` / `39257a4e`. It documents
only `computed-sql`, `computed-plugin`, `file-folder`, and validation, and still
presents raw `computed-sql` as the default recommendation. Two edits: (a) add a
section for the shipped `computed-expr` metadata key
(`src/BifrostQL.Abstractions/Model/MetadataKeys.cs:221` /
`ComputedColumnKind.Expression`) with a pointer to `guides/expression-builder`;
(b) mark `computed-sql` deprecated — it carries `[Obsolete]` at
`src/BifrostQL.Core/Modules/ComputedColumns/ComputedColumnDefinition.cs:19,116`
and is admin-gated at collection time — and demote it below the Expression kind.

**Verified covered — `guides/expression-builder`**: node table matches the
closed hierarchy in `QueryModel/SqlExpr.cs`; the `Fn` allow-list matches
`SqlExprFunctions` exactly; the only two `NotSupported` matrix cells (DateDiff
month/year on Postgres and SQLite) are the only two that throw
`SqlExprLoweringNotSupportedException`.

## 2. Security, tenancy, compliance

| Feature area | Page(s) | Status |
|---|---|---|
| Authentication — local, OIDC, JWT | `guides/authentication` | covered |
| **Authorization / policy engine** | — | **missing** |
| Multi-tenant org model | `guides/org-model` | covered |
| Field encryption — design and key hierarchy | `concepts/field-encryption` | **stale** |
| Field encryption — DEK rotation | `guides/field-encryption` | covered, one gap |
| **Blind-index read routing** | — | **missing** |
| Temporal change history — concept | `concepts/temporal-history` | covered |
| Change history — configuration | `guides/change-history` | covered |
| Retention & right-to-erasure | `guides/retention` | covered, one gap |

**Key-rotation content location (verified)**: commit `e4b5a580`
`docs(crypto): add key-rotation guide` landed **162 lines into
`docs/src/content/docs/guides/field-encryption.md`** plus one sidebar line in
`docs/astro.config.mjs`. It does cover the DEK-version-in-ciphertext envelope,
version-directed decryption, and the online re-encryption sweep — and the sweep
description matches
`src/BifrostQL.Core/Modules/Crypto/CryptoReEncryptionSweep.cs` (routes through
`IMutationIntentExecutor`, no direct SQL, no tenant-scope bypass, idempotent),
including reads-during-sweep and denied-role observability. The plan's Phase-0
question is answered: the guide is real and accurate for rotation.

**Fix note — `concepts/field-encryption`**: the page predates commit `84fc0311`
(`feat(crypto): route encrypted-column equality onto the blind index`). Its
"No plaintext oracle" section (~line 100) still calls server-side routing of an
equality predicate onto the blind index "a planned enhancement … the query-side
routing is the remaining piece", and "Searching encrypted columns" (~line 105)
still calls predicate rejection "a later slice". Both statements are now false.
Rewrite both sections against the shipped rewrite in
`src/BifrostQL.Core/Modules/QueryTransformerService.cs`, and state which
operators route (`_eq`, `_in`) versus which are still rejected.

**Fix note — blind-index read routing**: shipped in `84fc0311` and documented
**nowhere** in the docs tree. The equality/`_in` rewrite onto the `_bidx` column
needs a section in `guides/field-encryption` (or a short new
`guides/searching-encrypted-columns`) covering which operators route, what a
denied role observes, and that the blind-index key is stable across DEK
rotation. Note the `.claude/rules` provenance: this is the feature whose
revert-proof surfaced the forced-rebuild rule.

**Fix note — authorization/policy engine**: `guides/authentication` covers
identity only. `RowScopeCompiler`, `SchemaReadVisibility`, `TablePolicy`,
`PolicyEvaluator`, and `StateMachineRoleGate` (all under
`src/BifrostQL.Core/Auth/`) appear only as passing mentions inside the
protocol-adapter guides — the same evaluator every adapter's catalog surface is
required to call (protocol-adapter-security invariant 4) has no page of its own.
New page: `guides/authorization-policies`.

**Fix note — `guides/retention`**: content matches
`Modules/Retention/RetentionConfig.cs` and `RetentionPurgeEngine.cs` including
the exact dry-run signature. Missing: the purge engine is never documented as
auto-registered. Add a short "when purges actually run" section covering
`RetentionPurgeHostedService` / `RegisterRetentionPurgeServices`
(`BifrostServiceRegistrar.cs:388-397`), the default one-hour poll,
`DefaultBatchSize = 100`, and that it self-disables when no table opts in. As
written a reader cannot tell purges run automatically once a policy exists.

## 3. Workflow, approval, deferred effects

| Feature area | Page(s) | Status |
|---|---|---|
| Workflows | `guides/workflows` | covered |
| Workflow mutations & audit trail | `guides/workflow-mutations` | covered |
| State machines | `guides/state-machines` | covered |
| **Approval workflows (maker-checker)** | `guides/approval-workflows` | **stale + orphan + no description** |
| **Deferred / reversible change sets** | `guides/deferred-effects` | **stale + orphan + no description** |

**Fix note — `guides/approval-workflows`**: intercept/divert, per-action batch
pending rows, TreeSync per-node diversion, and the
`pending → approved|rejected|expired` state machine all match source. The stale
half is the decision surface: the page describes replay as unbuilt ("a later
approval/replay implementation must run it as that requester"), but
`Modules/Approval/ApprovalDecisionService.cs` ships
`ApproveAsync`/`RejectAsync`/`ExpireAsync` with requester-scoped replay and
`SchemaGenerator.cs:162-163` emits `approve(pendingChangeId: ID!)` and
`reject(pendingChangeId: ID!, reason: String!)`. Add a GraphQL decision-surface
section (both mutations, the required rejection reason, self-approve enforcement
at decision time), delete the "future work" framing, add a `description`, and
add the page to the sidebar.

**Fix note — `guides/deferred-effects`**: the contract, metadata, durable-store
columns, review-queue GraphQL, and LIFO/conflict semantics all match. The stale
half: the page says "Future capture and undo slices must implement this named
decision; this guide deliberately adds no write-path behavior" — but capture
(`Modules/Deferred/DeferredDeltaMutationHook.cs`, registered at
`BifrostServiceRegistrar.cs:89`) and undo (`Modules/Deferred/DeferredUndoEngine.cs`)
both ship, and `undo(changeSetId: ID!): DeferredUndoResult!`
(`SchemaGenerator.cs:166`) is undocumented. Also absent: the CDC outbox release
lifecycle — `Modules/Cdc/DeferredOutboxReleaseEngine.cs` and its
`DeferredOutboxReleaseHostedService` (`BifrostServiceRegistrar.cs:365,602`), and
the `pending_hold → pending | suppressed` states — which is only described
conceptually. Add the write-path and outbox-release sections, add a
`description`, and add the page to the sidebar.

## 4. Protocol adapters

| Adapter | Page(s) | Status |
|---|---|---|
| Adapter architecture (concept) | `concepts/protocol-adapters` | covered |
| Authoring an adapter | `guides/protocol-adapters` | covered |
| pgwire (PostgreSQL wire) | `guides/pgwire`, `guides/pgwire-bi-smoke` | covered |
| RESP (Redis wire) | `guides/resp`, `guides/resp-smoke` | covered |
| gRPC | `guides/grpc`, `concepts/grpc-schema-contract` | covered |
| OData v4 | `guides/odata` | covered |
| S3-compatible object endpoint | `guides/s3` | covered |
| Prometheus metrics | `guides/prometheus` | covered |
| Syndication feeds (RSS/Atom) | `guides/feeds` | covered |
| **LDAP directory endpoint** | `guides/ldap`, `guides/ldap-smoke` | **covered** |
| Binary transport (protobuf/WebSocket) | `guides/binary-transport` | covered |
| MCP server | `guides/mcp-server`, `guides/mcp-tool-authoring`, `reference/mcp-declarative-tools` | covered |

**LDAP is no longer a gap.** The content plan lists `guides/ldap.md` as missing;
it landed with the epic in commit `17f2be79`. Both pages are accurate: sections
cover simple-bind with anti-enumeration, cleartext-credential refusal, StartTLS
and LDAPS, paged results and the integrity-protected cookie, search bounds,
group membership / `memberOf`, and bind rate-limiting plus audit. Every option
named on the page (`AnonymousBindEnabled`, `LdapsPort`, `MaxSearchResults`,
`SearchBatchSize`, `MaxMembersPerEntry`, `MemberOfEnabled`,
`PagedResultsCookieSecret`, `AllowInsecureSimpleBind`,
`MaxBindAttemptsPerAccount`) exists in
`src/BifrostQL.Server/Ldap/LdapWireOptions.cs`, and unsupported controls are
stated as non-goals rather than claimed. Phase 2 can treat LDAP as a *polish and
promote* target, not a write-from-scratch one.

**`guides/feeds` verified covered** against `Feeds/FeedAuthenticator.cs` (single
uniform `401`, all classes collapsed — the anti-oracle property holds) and
`Feeds/FeedConditionalRequest.cs:46` (identity-partitioned strong ETag, epoch
`Last-Modified` for an empty feed).

## 5. Clients, UI, hosting

| Feature area | Page(s) | Status |
|---|---|---|
| Data workbench overview | `guides/workbench/index` | covered |
| Saved queries | `guides/workbench/saved-queries` | covered |
| Forms & subforms (UI) | `guides/workbench/forms` | covered |
| Tabular reports | `guides/workbench/printable-tables` | covered |
| SQL editor | `guides/workbench/sql-editor` | covered |
| ER diagram | `guides/workbench/erd` | covered |
| Export | `guides/workbench/export` | covered |
| Charts | `guides/workbench/charts` | covered, one nit |
| Pivot UI | `guides/workbench/pivot-ui` | covered |
| Dashboards | `guides/workbench/dashboards` | covered |
| Grid grouping | `guides/workbench/grouping` | covered |
| Visual query builder | `concepts/visual-query-builder` | covered |
| Desktop app | `guides/desktop-app` | covered |
| Hosted SPA / API mode | `guides/hosted-spa` | covered |
| Embeddable editor (`edit-db`) | `guides/embedded-editor` | covered |
| React hooks (`@bifrostql/react`) | `guides/react-hooks` | covered |
| React Native | `guides/react-native` | covered |
| Saved objects | `concepts/saved-objects` | covered |
| App-metadata overlay | `concepts/app-metadata-overlay` | covered |
| App schema detection | `concepts/app-schema-detection` | covered |
| WordPress | `guides/wordpress` | covered |
| **`@bifrostql/types`, `@bifrostql/app-shell`** | — | **missing** |

The workbench pages are fresh (commit `728a88b5`, plus corrections `9fa9647c`
and `1ecce6cb`). Spot-checks matched: the `MAX_CHART_CATEGORIES = 100` guard
(`chart-model.ts:30,85,89`), CodeMirror 6 with the Photino-bridge-only execution
boundary (`SqlConsole.tsx:10-26,124`), and the `gb` grouping URL param
(`lib/grid-grouping.ts`).

**Structural note for Phase 2/3 authors**: the workbench ships from *two* trees,
not one. Charts, pivot, dashboards, ERD, reports, the query designer, and the
SQL console live in `src/BifrostQL.UI/frontend`; grouping, export, and
forms/subforms live in `examples/edit-db/src`. Any grounded article or capture
scene touching those three features must drive the `edit-db` code path.

**Nit — `guides/workbench/charts`**: the page reads `dimensions` as plural, but
the query builder uses `dimensions[0]` and errors without one. Either say
single-dimension explicitly or fix the plural.

**Fix note — `@bifrostql/types` / `app-shell`**: zero hits anywhere in the docs
tree. Per AGENTS.md both `@bifrostql/react` and `app-shell` are *experimental*
parallel stacks and `app-shell` has no importers, so the right fix is a short
`reference/typescript-types` page plus one honest paragraph in an existing page
marking `app-shell` experimental — **not** a full guide that would read as an
endorsement of a non-canonical client stack.

## 6. AI and chat

| Feature area | Page(s) | Status |
|---|---|---|
| Chat over your tables (concept) | `concepts/chat` | covered |
| LLM chat endpoints | `guides/llm-chat` | covered |
| Chat connectors | `guides/chat-connectors` | covered |
| MCP server | `guides/mcp-server` | covered |
| MCP tool authoring | `guides/mcp-tool-authoring` | covered |
| Declarative MCP tool DSL | `reference/mcp-declarative-tools` | covered |

## 7. Extensibility, storage, tooling

| Feature area | Page(s) | Status |
|---|---|---|
| Module system | `guides/modules` | covered |
| Hooks & providers | `guides/extensibility` | covered |
| CDC outbound events (concept) | `concepts/cdc-outbound-events` | covered |
| CDC configuration | `guides/cdc-events` | covered |
| CLI tool (`BifrostQL.Tool`) | `guides/developer-guide` | covered but **orphan** |
| **File storage / file columns** | — | **missing** |
| **S3 as a backing store (`BifrostQL.Aws`)** | — | **missing** |
| **Form & view builders (server-side)** | — | **missing** |
| **9 out-of-the-box app schemas** | — | **missing** |
| **`getting-started/connect-a-database`** | — | **missing** |

**Fix note — file storage**: `Storage/` ships `IStorageProvider`,
`LocalStorageProvider`, file-column handling, and bucket config. The Starlight
site has none of it — only a legacy pre-Starlight file at
`docs/file-storage-system.md` and three config rows in `reference/configuration`
(`file`, `file-storage`, `storage`). Port the legacy doc to
`guides/file-storage`.

**Fix note — S3 backing store**: `src/BifrostQL.Aws/` ships `S3StorageProvider`
and `AwsStorageRegistration`, and nothing documents it. Note the direction of
the confusion: `guides/s3` covers the **inbound** S3-compatible *endpoint*
(serving file columns over the S3 wire), which is the opposite direction. Add
this as a section of the new `guides/file-storage`, and add a disambiguating
sentence to `guides/s3` so the two are not conflated.

**Fix note — form & view builders**: `Core/Forms/` and `Core/Views/` ship
`BifrostFormBuilder`, `ListViewBuilder`, `DetailViewBuilder`, and
`FileUploadHandler`. `guides/workbench/forms` documents the *workbench UI*, not
the server-side builder API. New page: `guides/form-and-view-builders`.

**Fix note — CLI**: `guides/developer-guide` already documents all nine
commands (`init`, `serve`, `schema`, `config-generate`, `config-validate`,
`doctor`, `watch`, `export`, `test`), but the page is not in the sidebar. Add it
— arguably split the command list out as `reference/cli` and leave the
debugging/logging material in the developer guide.

## 8. Out-of-the-box app schemas (all missing from docs)

No page in the docs tree documents the bundled schemas. `concepts/app-schema-detection`
and `concepts/app-metadata-overlay` cover the *mechanisms*;
`getting-started/examples` does not enumerate these. This is the largest single
content gap and maps directly to Phase 2 articles 4–8.

| Schema | Tables | Sample seed | Full seed | Postgres seed | Notes |
|---|---|---|---|---|---|
| `blog` | 6 | yes (202 L) | yes (2929 L) | no | |
| `classroom` | 6 | yes (292 L) | yes (4195 L) | no | |
| `crm` | 6 | yes (222 L) | yes (3132 L) | no | only schema shipping `crm.bifrost.json` app-metadata overlay |
| `ecommerce` | 7 | yes (267 L) | yes (4987 L) | no | |
| `membership-manager` | 16 | yes (367 L) | no | yes (545 L) | largest; used as the `guides/state-machines` example |
| `org-model` | 7 | yes (84 L) | no | yes (158 L) | has `guides/org-model` for the *pattern*, not the schema |
| `project-tracker` | 8 | yes (199 L) | yes (2825 L) | no | |
| `sqlite-advanced` | 8 | yes (160 L) | no | no | |

Note the plan says "9 schemas"; the directory holds **8 `.sql` schemas** plus
`crm.bifrost.json` (an app-metadata overlay, not a ninth schema). Phase 2's
roundup article should say eight.

**Fix note**: one page per schema under `getting-started/app-schemas/`, plus an
index. Each page needs: table list, which seed variants exist, which dialects
have seeds, the working connection string, and one working query — Phase 0 step
3 (verify each loads under `./bifrostui`) is **not yet done** and remains a
prerequisite for Phase 2 grounding.

---

## 9. Astro / SEO technical baseline (Phase 1 starting state)

Read from `docs/astro.config.mjs`, `docs/package.json`, and `docs/public/`.

| Item | Current state |
|---|---|
| `site` | `https://dev.standardbeagle.com` — set |
| `base` | `/BifrostQL` — set (every canonical URL must include it) |
| **Sitemap** | **ABSENT** — `@astrojs/sitemap` is not a dependency and not in `integrations` |
| **robots.txt** | **ABSENT** — `docs/public/` contains only `favicon.svg` |
| **OpenGraph / Twitter card tags** | **ABSENT** — no `head` entries in the Starlight config |
| **Default meta description** | **ABSENT** — no site-level fallback; pages without `description` emit none |
| **Canonical URL tags** | Starlight default only; not configured explicitly |
| **JSON-LD** (`TechArticle` / `HowTo` / `SoftwareApplication`) | **ABSENT** |
| OG image asset | **ABSENT** — nothing in `docs/public/` but the favicon |
| Custom components | `Header.astro` override only |
| Custom CSS | `src/styles/custom.css` |
| Deps | `@astrojs/starlight ^0.41.5`, `astro ^7.1.5`, `sharp ^0.35.3` |

So the entire Phase-1 "technical baseline" PR is greenfield: sitemap, robots,
OG/Twitter, default description, and JSON-LD all have to be added, none modified.

### Frontmatter audit (all 80 pages)

| Metric | Count |
|---|---|
| Pages | 80 |
| Missing `title` | 0 |
| `title` under 50 chars | 77 |
| `title` 50–60 chars | 2 |
| `title` over 60 chars | 1 |
| **Missing `description`** | **2** (`guides/approval-workflows`, `guides/deferred-effects`) |
| `description` under 150 chars | 22 |
| `description` 150–170 chars | 4 |
| `description` over 170 chars | 52 |

The dominant defect is not absence but *shape*: nearly every title is a bare
noun (`Joins`, `Queries`, `Mutations`, `Workflows`) with no keyword, and 52
descriptions are long enough to be truncated in every SERP — several run past
400 characters. Per-page targets are in `keyword-map.md`.

### Sidebar orphans

`guides/approval-workflows`, `guides/deferred-effects`, and
`guides/developer-guide` exist as pages but have no entry in
`docs/astro.config.mjs`. They are reachable only by direct URL and accrue no
internal link equity. Two of the three are also the two pages with no
`description`. Fixing the sidebar is a Phase 1 prerequisite.

---

## 10. Phase 1 / Phase 2 prerequisites this inventory surfaces

1. Three pages into the sidebar; two `description` fields written.
2. Six stale sections corrected against source before any SEO pass touches them
   (blind-index routing ×2, computed `Expression` kind, approval decision
   surface, deferred write path, retention hosted service).
3. Eight new pages scoped: authorization policies, blind-index search, file
   storage (incl. S3 backing), form/view builders, TypeScript types,
   connect-a-database, app-schemas index + 8 schema pages.
4. Phase 0 step 3 (load each seeded schema under `./bifrostui`, capture a
   working connection string and query) is **still outstanding** and gates the
   grounding rule for Phase 2 articles 4–8.
