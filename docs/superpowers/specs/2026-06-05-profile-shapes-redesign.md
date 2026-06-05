# Profiles as Per-Profile Schema Shapes — Design Spec

Date: 2026-06-05
Status: Proposed (supersedes the 2026-06-03 CRM-showcase design and the API-profiles/App-DB-view work)

## 1. Problem

The current "API profile" / "App vs DB view" work (commit `8ed48b6` and the
uncommitted session churn on top of it) is incoherent:

- "Database (raw)" is not raw — it still runs soft-delete (deleted rows hidden),
  because a request with no `?profile=` is treated as *all transformers active*
  while schema-modules (polymorphic) are *off*. Opt-in filters ON, opt-in joins
  OFF — contradictory.
- A separate hand-rolled "App view" (`AppView.tsx`, overlay JSON) duplicated the
  real editor and was removed mid-session, leaving dead concepts (`overlayUrl`,
  `defaultView`).
- Polymorphic joins leaked across types in the editor because the editor drills
  into children by the id column alone; gating was bolted on per-request while
  the `_dbSchema` discovery path stayed ungated, producing `Cannot query field
  'notes'` mismatches.

Root cause: "profile" was modeled as an ad-hoc bundle (overlay + view + a partial
module gate) instead of a single coherent concept.

## 2. The model

**A profile is a named metadata overlay that produces its own GraphQL schema.**

Per request, the active profile selects a metadata set + module set; the server
builds (and caches) the model and schema for that (connection, profile). Nothing
is "globally on" — every shape is a profile.

A profile therefore controls, as one unit:
- **Visible surface** — which tables and columns appear (reduced vs full set).
- **Relationships** — FK joins (always, from the DB) plus opt-in joins such as
  the polymorphic discriminator join.
- **Data-shaping modules** — soft-delete, tenant-filter, policy, etc.

This makes profiles the mechanism for different **object shapes per consumer**:

| Profile | Metadata | Shape |
|---|---|---|
| `dev` | none | full raw database — all tables, all columns, FK joins only, no soft-delete, no polymorphic |
| `admin` | polymorphic map | full columns, polymorphic joins, soft-delete off |
| `web` / `sales` | hide internal tables/cols + polymorphic map + soft-delete | reduced curated shape for an app UI |
| `mobile` | tighter column set, fewer joins | minimal payload shape |
| `etl` | full columns, no joins, no soft-delete | flat extract shape |

`dev` is just the empty profile (no overlay). There is no synthetic "no-profile"
path with special semantics.

## 3. Configuration

Profiles are named config entries. Each carries its **own** metadata and module
set (today metadata is one shared block — that moves into each profile).

Desktop per-connection config (`Schemas/<schema>.bifrost.json`):

```json
{
  "profiles": [
    { "name": "dev",   "label": "Dev (raw database)", "metadata": [], "modules": [] },
    { "name": "sales", "label": "Sales",
      "metadata": [
        "*.notes { polymorphic-type-column: entity_type; polymorphic-id-column: entity_id; polymorphic-map: company=companies, contact=contacts, deal=deals }",
        "*.deals { soft-delete: deleted_at }",
        "main.internal_audit { hidden: true }"
      ],
      "modules": ["polymorphic", "soft-delete", "policy"] },
    { "name": "admin", "label": "Admin (all records)",
      "metadata": [ "*.notes { polymorphic-... }" ],
      "modules": ["polymorphic", "policy"] }
  ]
}
```

Generic BifrostQL hosts: same shape under a `BifrostQL:Profiles` config section,
each profile with `metadata` + `modules` (+ optional `requireRole`).

Notes:
- `metadata` is a list of the existing rule strings (same grammar as today's
  global `BifrostQL:Metadata`). Module activation is driven by `modules`; the
  metadata supplies the per-table configuration those modules read.
- The first profile (or one flagged default) is the initial selection.

## 4. Capabilities to build

1. **Per-profile metadata → per-profile model/schema.** Replace the single
   global/runtime metadata with metadata resolved from the active profile. Build
   the model+schema for (connection, profile) and cache it; base DB introspection
   is shared across profiles (done once), profile metadata is layered on top.
2. **Schema-gen honors visibility.** `MetadataKeys.Ui.Hidden` / `Visibility` must
   drop tables and columns from a profile's schema (currently the keys exist but
   schema generation ignores them). This is the "reduced table/column set".
3. **Opt-in relationships gated by module + present consistently.** Polymorphic
   joins appear only when the profile activates `polymorphic`, and must appear
   identically in BOTH the executable schema AND the `_dbSchema` discovery the
   editor introspects (no mismatch → no `Cannot query` errors).
4. **Selection.** `?profile=` / `X-BifrostQL-Profile` header selects the profile;
   the desktop picker sends it; the editor re-introspects per profile. No
   implicit all-on default.
5. **Polymorphic module — both addressing modes** (carried over, keep): paired
   (discriminator + id) and globally-unique-id + table list. The client joins by
   id only; the server supplies the discriminator. The editor drill-down must
   traverse the relationship (id-keyed), not raw-filter the child by the FK
   column — fix in `examples/edit-db` so it cannot bypass the discriminator.

## 5. Reset & cleanup (Phase 0 — do first)

Throw away the confused mess and restart from a clean baseline.

- **Baseline commit: `c3151e1`** (`feat(mutations): nested object-tree sync`).
  Keeps: the CRM sample schema/seed (`e2d3be3`, far older), the polymorphic Core
  join strategy (`8694efe`), mutations, many-to-many. Drops: `8ed48b6`
  (API profiles + App/DB views + overlay + AppView/ViewToggle/ProfileDropdown)
  and every uncommitted session change layered on it.
- **Re-apply** the one good post-baseline fix: `e6faaf7`
  (`fix(ui): flush trailing SSE event`) — cherry-pick onto the fresh branch.
- **Discard** these confused artifacts (all from `8ed48b6` / session): the
  `src/BifrostQL.UI/frontend/src/profiles/` directory, `crm-sales.json` overlay,
  the App-view CSS block in `app.css`, the App.tsx profile/view wiring, and the
  `2026-06-03-polymorphic-join-and-crm-showcase.md` design doc.
- **Docs cleanup**: remove/replace stale design docs and CLAUDE.md notes that
  describe overlays / App-DB views / "API shapes". Update `SKILLS.md` /
  `CLAUDE.md` quick-reference once profiles land. Keep the blog-tenant +
  file-thumbnail spec only if it is re-scoped onto this model (tenant becomes a
  profile; otherwise move to a backlog doc).
- Do this on a new branch off `c3151e1`; verify the full test suite is green at
  baseline before building.

Work in a git worktree off `c3151e1` so the current tree is preserved for
reference until the rebuild lands.

## 6. Server architecture

- **Model load**: introspect the DB once per connection → base model (FK /
  name-based / m2m links, all tables/columns). Cache per connection.
- **Profile schema**: for (connection, profile), apply the profile's metadata to
  a model view, run opt-in relationship strategies whose module is active
  (polymorphic), apply visibility, and generate the `ISchema`. Cache per
  (connection, profile). Invalidate when the connection (model) is rebound.
- **`_dbSchema`**: derive from the same profile-resolved model view so the
  editor's discovery matches the executable schema exactly.
- **Middleware**: resolve the profile, then resolve the profile's schema +
  active transformers from one place. Remove the "name null/`default` → all
  active" branch.

## 7. Out of scope (backlog)

- File-storage thumbnails (separate spec; revisit after profiles).
- Mock OIDC / real tenant identity (tenant stays a dev-gated profile context).
- Cross-endpoint profiles (re-pointing the data source).

## 8. Testing

- Profile schema gen: `dev` schema has all base tables/cols + FK joins, NO
  polymorphic join; `sales` schema hides the configured tables/cols and HAS the
  polymorphic join; `admin` has full cols + polymorphic.
- Visibility: a `hidden: true` table/column is absent from that profile's schema
  and from `_dbSchema`.
- Polymorphic isolation (carry over `PolymorphicLeakRepro`): a parent's notes
  collection returns only its own discriminator rows, through the relationship.
- Data-shaping per profile: soft-delete active under `sales` (deleted rows
  hidden), inactive under `dev`/`admin`.
- `_dbSchema` ≡ executable schema per profile (no field advertised that the
  schema rejects).
- All existing suites green at baseline and after each phase.

## 9. Build discipline

WSL2 incremental builds have served stale code here repeatedly (an "up-to-date"
build ran old code mid-session). Clean-rebuild (`rm -rf src/BifrostQL.*/bin
src/BifrostQL.*/obj` + the test project's bin/obj) before trusting any test
result. Backend dev runs under `dotnet watch` via `.agnt.kdl`
(`DOTNET_USE_POLLING_FILE_WATCHER=true`); do not start a competing `dotnet run`
on port 5000.
