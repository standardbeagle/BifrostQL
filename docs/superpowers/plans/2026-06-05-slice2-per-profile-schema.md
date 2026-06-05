# Slice 2 — Per-(connection,profile) model + schema

> REQUIRED SUB-SKILL: superpowers:executing-plans / subagent-driven-development. Worktree: `/home/beagle/work/core/bifrost-profiles-shapes` (branch `profiles-shapes`). Clean-rebuild before trusting tests (WSL2 stale-build).

**Goal:** Each request's active profile gets a model+schema built from the *base DB read* + that profile's own metadata, cached per (connection, profile). The empty/`dev` profile = base read only. No "no-profile = all-on" path.

**Why model, not just schema:** metadata shapes the model at build time (visibility hiding in `DbModel.FromTables`, polymorphic links via strategies, soft-delete column markers). So per-profile metadata ⇒ per-profile model. The expensive DB *read* (schema reader) is shared across profiles; only `DbModel.FromTables` + strategy passes re-run per profile.

**Baseline facts (from exploration):**
- `PathCache<Inputs>` (`src/BifrostQL.Core/Schema/PathCache.cs`) caches one `Inputs{model,connFactory,dbSchema}` per path; `ResetAll()` on reconnect.
- `DbModelLoader.LoadAsync(additionalMetadata)` = read (`connFactory.SchemaReader.ReadSchemaAsync`) then `DbModel.FromTables(tables, metadataLoader, …, additionalMetadata)`.
- `DbSchema.FromModel(IDbModel)` → `SchemaGenerator.SchemaTextFromModel` + `BifrostDispatcher.WireResolvers`.
- Middleware `BifrostDocumentExecutor.ExecuteAsync`: resolves profile (before schema), then `ResolveExtensions` → `options.Schema = sharedExtensions["dbSchema"]`, model from `["model"]`. `ResolveProfileName`: header `X-BifrostQL-Profile` > `?profile=` > path; null/"default" ⇒ no filtering.
- `BifrostProfile.Metadata` (added Slice 1), `Modules`, `IsModuleActive`. Registry: `Add/Get/HasProfiles/FilterBy` (no ReplaceAll/Clear/All).

---

### Task 2.1 — Split DB read from model build in `DbModelLoader`

**Files:** `src/BifrostQL.Core/Model/DbModelLoader.cs`; test `tests/BifrostQL.Core.Test/Unit/Model/DbModelLoaderSplitTests.cs`.

Add (keep `LoadAsync` working, delegating):
- `Task<SchemaReadResult> ReadAsync(CancellationToken)` — does the connection read only (`SchemaReader.ReadSchemaAsync`), returns the raw tables+FKs (introduce a small `SchemaReadResult` record wrapping `schemaData` if one isn't already returned by the reader; reuse the reader's existing return type if possible).
- `IDbModel BuildModel(SchemaReadResult read, IMetadataLoader metadataLoader, IDictionary<string,IDictionary<string,object?>>? additionalMetadata)` — runs `DbModel.FromTables(...)` + sets TypeMapper. `LoadAsync` becomes `BuildModel(await ReadAsync(), _metadataLoader, additionalMetadata)`.

**Test:** one `ReadAsync` (sqlite crm), then `BuildModel` twice with two different `MetadataLoader`s (one empty, one with `*.notes { polymorphic-... }`) → first model's `companies.MultiLinks` lacks `notes`, second has it. Proves read-once/build-many with per-metadata divergence.

---

### Task 2.2 — `DbSchema.FromModel` profile-aware overload

**Files:** `src/BifrostQL.Core/Schema/DbSchema.cs`; extend an existing schema test.

Add overload `FromModel(IDbModel model, BifrostProfile? profile)`; keep `FromModel(IDbModel)` delegating with `profile: null`. For now `profile` only flows through (no gating — Slice 3 consumes it). This keeps the signature stable for the cache + the carried-over `PolymorphicLeakRepro` (which already calls `FromModel(model, profile)` — restore that test in Slice 3; do NOT add it here).

**Test:** `FromModel(model, new BifrostProfile{Name="dev"})` returns a working schema identical to `FromModel(model)` at this slice (smoke: companies type present).

---

### Task 2.3 — `ProfileModelCache` (per connection → per profile)

**Files:** `src/BifrostQL.Core/Schema/ProfileModelCache.cs` (new); test `tests/BifrostQL.Core.Test/Unit/Schema/ProfileModelCacheTests.cs`.

A cache that, given: a base-read provider (`Func<SchemaReadResult>` or the loader), the base config metadata rules (string[]), the `BifrostProfileRegistry`, and a profile name → returns `(IDbModel model, ISchema schema)` built once per profile name and memoized. Building a profile:
- metadata = base config rules **+** the profile's `Metadata` (empty for unknown/`dev`).
- `model = loader.BuildModel(read, new MetadataLoader(metadata), additionalMetadata)`.
- `schema = DbSchema.FromModel(model, profile)`.
- Key by profile name (case-insensitive); `Reset()` clears all (called on reconnect). Lock-free snapshot or simple lock; the read is shared (built once).

**Test:** request profile "dev" (no metadata) and a "poly" profile (registry has it with `Metadata=["*.notes {polymorphic-...}"]`, `Modules=["polymorphic"]`) against one crm read → dev model `companies` has no `notes` MultiLink, poly model has it; requesting "dev" twice returns the same cached instance.

---

### Task 2.4 — Wire the cache into `Extensions.ConfigureServices`

**Files:** `src/BifrostQL.Server/Extensions.cs` (both `BifrostSetupOptions` and `BifrostMultiDbOptions` loaders).

- The `PathCache` loader keeps doing the DB **read once** and stores it (plus connFactory, base metadata rules) so the `ProfileModelCache` can build profile models without re-reading. Simplest: store the `SchemaReadResult` + a configured base-`DbModelLoader` in `Inputs` (alongside the existing default `model`/`dbSchema` which remain the `dev`/base build).
- Register `ProfileModelCache` (singleton, per connection — reset via the existing `ResetSchema`/`PathCache.ResetAll` path; have `ResetSchema` also reset the ProfileModelCache).

**Test:** Server-level (or via existing harness) — a profile’s schema differs from base after wiring; reconnect resets.

---

### Task 2.5 — Middleware resolves schema per profile; drop no-profile all-on

**Files:** `src/BifrostQL.Server/BifrostHttpMiddleware.cs`.

- After resolving the profile name, fetch `(model, schema)` from `ProfileModelCache` for that name (name null ⇒ the base/`dev` empty profile). Set `options.Schema` + the `model`/`tableReaderFactory` extensions from that profile build.
- Keep transformer filtering, but source the active `BifrostProfile` consistently (registry.Get(name) ?? empty dev profile). Remove the branch that treats null/"default" as "all transformers active": the base/dev profile has empty modules ⇒ no opt-in transformers. (Confirm `policy` built-in behavior is acceptable off under dev; it is a no-op without policy metadata.)
- `_dbSchema` already derives from the model in extensions → now the per-profile model → consistent with the executable schema.

**Test:** Server harness — `?profile=poly` query/introspection sees `notes`; no-profile / `?profile=dev` does not; a query for `notes` under dev errors at validation (field absent) rather than leaking.

---

### Task 2.6 — Slice verify

- Clean-rebuild; Core + Server + UI suites green.
- Manual/live (optional): `curl /graphql?profile=dev` vs a polymorphic profile shows the field difference; `_dbSchema` matches.
- Commit each task; final commit `feat(profiles): per-(connection,profile) model+schema with shared DB read`.

---

## Notes / risks
- `DbModelLoader` split must not change `LoadAsync` semantics (used across the codebase). Add methods; delegate.
- `additionalMetadata` (from `_metadataSources`/CompositeMetadataSource) still applies to every profile build (it's connection-level). Keep passing it.
- Per-profile build cost = `FromTables` + strategies (no DB I/O) — acceptable, memoized.
- Defer: polymorphic module-gating (Slice 3), visibility-from-profile-metadata already works via `FromTables` (hidden tables) — verify columns too in Slice 4.
