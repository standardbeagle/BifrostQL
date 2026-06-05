# Profiles-as-Shapes — Sliced Implementation Roadmap

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans or superpowers:subagent-driven-development per slice. Each slice is an independent, shippable, test-green plan. Detail later slices with writing-plans when reached.

**Goal:** Rebuild "profiles" cleanly as per-profile metadata overlays that each produce their own GraphQL schema (object shapes per consumer: dev/admin/web/mobile/etl), starting from a clean git baseline.

**Spec:** `docs/superpowers/specs/2026-06-05-profile-shapes-redesign.md`

**Architecture:** Base DB model introspected once per connection; each profile layers its own metadata + module set to produce a cached per-(connection,profile) schema. Selection via `?profile=`/header. No implicit "all-on" default.

**Tech Stack:** .NET 8/9/10 (BifrostQL.Core/Server/UI), SQLite quickstarts, React (edit-db), xUnit + FluentAssertions, agnt dev orchestration (`.agnt.kdl`).

**Ground rules (this roadmap):**
- Leave the sample SQL/seed files **unchanged**. Profiles drive shapes via config metadata, not by editing sample schemas. Sample-specific demo data (e.g. a soft-delete column) is a later, explicit slice.
- One slice at a time: each ends green (Core/Server/UI suites) and is committed before the next.
- WSL2: clean-rebuild (`rm -rf src/BifrostQL.*/bin src/BifrostQL.*/obj` + test proj bin/obj) before trusting results. Backend dev = `dotnet watch` via `.agnt.kdl`; never start a competing `dotnet run` on :5000.

---

## Slice roadmap (execute in order)

- **Slice 0 — Reset to clean baseline** (this doc, detailed below).
- **Slice 1 — Profile carries its own metadata + modules** (config model; no schema change yet). Detailed below.
- **Slice 2 — Per-(connection,profile) schema cache + selection.** Build/cache schema per active profile; `?profile=` selects; remove the no-profile "all-on" path; empty `dev` profile = base schema. `_dbSchema` derives from the same profile view.
- **Slice 3 — Polymorphic as a profile-gated module.** Tag polymorphic links; emit only when the profile activates `polymorphic`; consistent in executable schema AND `_dbSchema`. Carry over both addressing modes (paired + global-unique-id).
- **Slice 4 — Schema-gen honors `hidden`/visibility.** Drop tables/columns from a profile's schema + `_dbSchema` (reduced sets).
- **Slice 5 — Data-shaping modules per profile.** soft-delete / tenant gated by the active profile's module set (no global all-on).
- **Slice 6 — Desktop profile picker + per-connection profile config.** `/api/profiles` from the registry; picker sends `?profile=`; sample ships `<schema>.bifrost.json` profiles (dev/admin/sales) — still no sample-SQL edits.
- **Slice 7 — edit-db drills via the relationship.** Fix `examples/edit-db` child drill-down to traverse the parent relationship (id-keyed, server-scoped), not raw-filter the child by the FK column.
- **Slice 8 (optional demo data) — soft-delete column on a sample + polymorphic demo config.** Only here do we touch a sample's SQL, as an explicit opt-in slice.

Each slice 2–8 gets its own `docs/superpowers/plans/` doc written via writing-plans when reached.

---

## Slice 0 — Reset to clean baseline

**Files:** none edited in the current tree; work happens in a new worktree.

- [ ] **Step 1: Confirm baseline is green-able.** From repo root:
```
git log --oneline -1 c3151e1
```
Expected: `c3151e1 feat(mutations): nested object-tree sync (insert + reconcile)`.

- [ ] **Step 2: Create an isolated worktree off the baseline** (use the superpowers:using-git-worktrees skill). Target: a new branch `profiles-shapes` at `c3151e1`, in a sibling worktree dir. The current `main` tree (confused mess) is left untouched for reference.

- [ ] **Step 3: Cherry-pick the one good post-baseline fix.** In the worktree:
```
git cherry-pick e6faaf7
```
Expected: applies `fix(ui): flush trailing SSE event` cleanly. If it conflicts (it touches Program.cs SSE only), resolve to keep the SSE flush.

- [ ] **Step 4: Remove stale design docs from the baseline** that describe the discarded overlay/App-DB-view model:
```
git rm docs/superpowers/specs/2026-06-03-polymorphic-join-and-crm-showcase.md 2>/dev/null || true
```
(Only if present at this commit.) Copy the two new specs/this roadmap into the worktree (they live only in the messy tree): re-create `docs/superpowers/specs/2026-06-05-profile-shapes-redesign.md` and this roadmap in the worktree.

- [ ] **Step 5: Verify baseline + cherry-pick build clean.**
```
rm -rf src/BifrostQL.*/bin src/BifrostQL.*/obj
dotnet build BifrostQL.sln
```
Expected: Build succeeded, 0 errors.

- [ ] **Step 6: Verify baseline tests green.**
```
dotnet test tests/BifrostQL.Core.Test/BifrostQL.Core.Test.csproj -f net10.0
dotnet test tests/BifrostQL.Server.Test/BifrostQL.Server.Test.csproj -f net10.0
dotnet test tests/BifrostQL.UI.Tests/BifrostQL.UI.Tests.csproj -f net10.0
```
Expected: all pass (note any pre-existing baseline skips/failures; the `HealthEndpoint_ShowsConnectedStatus` env test only fails if a live :5000 server is up).

- [ ] **Step 7: Confirm the confused artifacts are absent at baseline** (they were never committed before `8ed48b6`, so a `c3151e1` worktree should not contain them):
```
test ! -e src/BifrostQL.UI/frontend/src/profiles && echo "OK: no profiles/ dir"
test ! -e src/BifrostQL.UI/frontend/public/overlays/crm-sales.json && echo "OK: no overlay"
```
Expected: both OK.

- [ ] **Step 8: Confirm samples are pristine.**
```
grep -c deleted_at src/BifrostQL.UI/Schemas/crm.sql   # expect 0
ls src/BifrostQL.UI/Schemas/*.sql                       # blog, classroom, crm, ecommerce, project-tracker, ...
```

- [ ] **Step 9: Commit the baseline-prep** (docs only):
```
git add docs/superpowers
git commit -m "docs: profiles-as-shapes spec + roadmap; reset to clean baseline"
```

Slice 0 done when the worktree builds, tests are green, and the confused artifacts are absent.

---

## Slice 1 — Profile carries its own metadata + modules

**Files:**
- Modify: `src/BifrostQL.Core/Modules/BifrostProfile.cs` (add `Metadata` to `BifrostProfile`)
- Test: `tests/BifrostQL.Core.Test/Unit/Modules/BifrostProfileMetadataTests.cs` (new)

> Note: at baseline `c3151e1`, `BifrostProfile` exists (Name/Modules/RequireRole) and `BifrostProfileRegistry` has Add/Get/HasProfiles only (no ReplaceAll/Clear/ContextValues/All — those were session churn and are intentionally gone; re-add what later slices need, when they need it).

- [ ] **Step 1: Write the failing test.**
```csharp
using BifrostQL.Core.Modules;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

public class BifrostProfileMetadataTests
{
    [Fact]
    public void Profile_CarriesOwnMetadataRules()
    {
        var p = new BifrostProfile
        {
            Name = "sales",
            Modules = new[] { "polymorphic" },
            Metadata = new[] { "*.notes { polymorphic-id-column: entity_id }" },
        };

        p.Metadata.Should().ContainSingle()
            .Which.Should().Contain("polymorphic-id-column: entity_id");
    }

    [Fact]
    public void Profile_DefaultsToNoMetadata()
    {
        var p = new BifrostProfile { Name = "dev" };
        (p.Metadata ?? System.Array.Empty<string>()).Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run, verify it fails.**
```
dotnet test tests/BifrostQL.Core.Test/BifrostQL.Core.Test.csproj -f net10.0 --filter BifrostProfileMetadataTests
```
Expected: FAIL — `BifrostProfile` has no `Metadata`.

- [ ] **Step 3: Implement.** In `BifrostProfile.cs`, add to the `BifrostProfile` class:
```csharp
/// <summary>
/// Metadata rules (same grammar as the BifrostQL:Metadata config section) that
/// define this profile's shape: visible tables/columns, opt-in joins, and the
/// per-table configuration its modules read. Null/empty = no overlay (the raw
/// base schema). Applied when building this profile's schema.
/// </summary>
public IReadOnlyList<string>? Metadata { get; init; }
```

- [ ] **Step 4: Run, verify pass.**
```
dotnet test tests/BifrostQL.Core.Test/BifrostQL.Core.Test.csproj -f net10.0 --filter BifrostProfileMetadataTests
```
Expected: PASS (2 tests).

- [ ] **Step 5: Clean-rebuild + full Core suite green.**
```
rm -rf src/BifrostQL.Core/bin src/BifrostQL.Core/obj tests/BifrostQL.Core.Test/bin tests/BifrostQL.Core.Test/obj
dotnet test tests/BifrostQL.Core.Test/BifrostQL.Core.Test.csproj -f net10.0
```
Expected: all pass.

- [ ] **Step 6: Commit.**
```
git add src/BifrostQL.Core/Modules/BifrostProfile.cs tests/BifrostQL.Core.Test/Unit/Modules/BifrostProfileMetadataTests.cs
git commit -m "feat(profiles): profile carries its own metadata rules"
```

Slice 1 done. Next: write the Slice 2 plan (per-profile schema cache + selection).

---

## Self-review notes
- Spec coverage: Slice 0 = §5 reset/cleanup; Slice 1 = §3 config model (Metadata on profile); Slices 2–7 = §2/§4/§6 model+schema+visibility+selection+editor; Slice 8 = §3 sample demo data (deferred per "leave examples alone"). Backlog (§7) untouched.
- The roadmap intentionally re-derives in clean slices what the session churn did messily (per-profile schema cache, polymorphic gating) rather than salvaging that code.
- Object-shape use cases (dev/admin/web/mobile/etl) are realized by Slices 1–6 (metadata + visibility + modules per profile); no extra mechanism needed.
