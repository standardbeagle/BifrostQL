# App-Builder Extraction Plan — Lessons from the Membership Manager

## Summary

The Membership Manager club app (`samples/HostedSpa/`) was built end-to-end on
BifrostQL as a deliberate forcing function: build one real app, see which parts
fall out of metadata for free and which parts still need hand-written code, and
let that boundary define the future app builder.

This document records that boundary at MVP completion. It splits the work into
three buckets:

1. **Proven reusable** — already metadata-driven or already a shared package
   primitive. The app builder can emit this; no further design needed.
2. **Extraction candidates** — required app-specific React/C# code in the club
   app, but the *shape* is general. These are the primitives the app builder
   must grow, in priority order.
3. **Club-specific product logic** — genuinely domain logic. Should *not* be
   generalized; the app builder's job is to make this kind of code easy to
   write, not to absorb it.

The guiding rule from the task scope holds: **keep app-builder UI out of the
MVP unless a primitive is already proven in the club app.** Everything in
bucket 1 is proven. Buckets 2 and 3 are post-MVP.

## Inventory: what was built

| Area | Location | Form of implementation |
|------|----------|------------------------|
| Entity overlay (11 entities) | `samples/HostedSpa/membership-manager.appmetadata.json` | Pure metadata |
| Field controls (scalar/date/bool/enum/json/fk) | `packages/@bifrostql/app-shell/src/fields/` | Shared package |
| Entity list / form / detail screens | `packages/@bifrostql/app-shell/src/screens/` | Shared package |
| App shell: nav, routing, session, protected routes | `packages/@bifrostql/app-shell/src/{nav,routing,auth}/` | Shared package |
| Metadata contract types | `packages/@bifrostql/types/` | Shared package |
| Member form/list field derivation | `spa/src/members/{member-form-fields,member-list-filters}.ts` | App-specific code |
| Saved-view picker | `spa/src/members/saved-views.ts` | App-specific code |
| Household-members relationship editor | `spa/src/households/household-members.tsx` | App-specific code |
| Event RSVP editor | `spa/src/events/event-rsvps.tsx` | App-specific code |
| Event check-in (mobile) | `spa/src/events/event-checkin.tsx` | App-specific code |
| Finance-field read gating | `spa/src/membership-plans/finance-fields.ts` | App-specific code |
| CSV export | `spa/src/exports/csv-export.ts` | App-specific code |
| Email-segment picker | `spa/src/segments/segment-definitions.ts` | App-specific code |
| Reports (dues, renewals, attendance, etc.) | `spa/src/reports/` | App-specific code |
| Dashboard | `spa/src/dashboard/` | App-specific code |
| Workflow endpoints (payment, renew, check-in, link-identity) | `samples/HostedSpa/MembershipWorkflowEndpoints.cs` | App-specific C# |
| Policy / finance column deny | `appsettings.json` + table metadata | Pure metadata (host side) |

## Bucket 1 — Proven reusable (app builder can emit today)

These required **zero app-specific code** in the club app, or are already
factored into a shared package. The app builder can generate or wire these
directly.

- **Entity overlay** — labels, icons, nav placement, field groups, widget
  hints, help text, display fields. All 11 entities are described entirely in
  `membership-manager.appmetadata.json`; the host's `System.Text.Json` reader
  ignores unknown keys, so the overlay is additive and safe.
- **Field control set** — `FieldControl` + `resolveFieldKind` map a `widget`
  hint to one of six controls (scalar, date, boolean, enum, json, fk).
  Unknown/absent widgets fall back to scalar, so a field always renders.
- **Entity list / form / detail screens** — `EntityList`, `EntityForm`,
  `EntityDetail` render from overlay metadata; form field ordering is overlay-
  driven (`displayFields` then declaration order), hidden fields dropped.
- **App shell** — `AppNav` (entity-driven nav), `AppLayout`, `ProtectedRoute`,
  `SessionProvider`/`useSession`. Standard for any overlay-described app.
- **Grid presets** — `defaultColumns`, `defaultSort`, `defaultFilters`,
  `bulkActions` are read straight from `grid` metadata.
- **Host-side policy** — `policy-read-deny` / `policy-read-deny-roles` on
  finance columns, `tenant-filter`, `policy-row-scope`. Pure table metadata; no
  app code. The *server* enforces it regardless of the client.
- **FK relationship declarations** — `foreignKeySelector` and `childCollection`
  relationships are declared in the overlay (`relationships` map); the data
  shape is metadata even though the *picker UI* is not (see bucket 2).

## Bucket 2 — Extraction candidates (prioritized)

Each of these required hand-written code in the club app, but the pattern
recurred across entities — which is the signal that it belongs in a primitive.
Listed highest-priority first. Priority = (frequency of recurrence) ×
(boilerplate eliminated) × (risk of divergence between hand-rolled copies).

### P1 — Relation picker primitive

**Evidence:** `household-members.tsx` and `event-rsvps.tsx` both hand-roll the
same thing: load a child collection, load the FK-target roster, resolve FK ids
to display names, render an `fk-lookup` `FieldControl` over candidates, and
wire insert/update/delete mutations. `event-checkin.tsx` is a third variation
(roster lookup + create). The raw FK id is never shown — that name-resolution
step is copy-pasted three times.

**Extract:** a `<RelationPicker>` / `<ChildCollectionEditor>` that takes a
`childCollection` relationship from the overlay and renders the list + add +
inline-edit + remove flow. The overlay already declares `targetEntity`,
`foreignKeyField`, and `displayColumns` — enough to drive it. FK-id→label
resolution should be a shared hook.

**Gap it closes:** *relation pickers*, *nested forms* (the editor mounted
inside a parent form is exactly a nested form).

### P2 — Enum option source in the overlay

**Evidence:** `household-members.tsx` hardcodes `RELATIONSHIP_ROLES`,
`event-rsvps.tsx` hardcodes `RESPONSE_OPTIONS`. Both note in comments that the
overlay declares the field as `select` but **carries no enum option set**, so
the vocabulary is supplied in app code (read out of `helpText` prose by a
human). `member-list-filters.ts` goes further and *reverse-engineers* candidate
values by regex-parsing saved-view filter expressions.

**Extract:** add an `options` (or `enum`) array to `FieldMetadata` for `select`
widgets. This is a small, safe additive metadata change that removes hardcoded
vocabularies and the filter-expression-scraping hack at once.

**Gap it closes:** *validation* (enum membership), *saved views* (filter
controls stop guessing values).

### P3 — Saved-view + filter-expression engine

**Evidence:** `saved-views.ts`, `member-list-filters.ts`, and
`segment-definitions.ts` each independently re-implement the **same** parser:
`/^\s*(\w+)\s*=\s*(.+?)\s*$/` over opaque `field = value` strings, only the
equality form understood. The overlay's filter expressions are deliberately
opaque client-interpreted strings, but three modules now parse them with
duplicated, intentionally-limited logic. Saved views with `now`/`now+30d`
relative dates (see `member_memberships` overlay) are *declared* but the parser
silently drops them.

**Extract:** one shared filter-expression parser/evaluator in `@bifrostql/app-shell`
(or `@bifrostql/types`) that produces a `TableFilter`, supports equality plus
the relative-date and comparison forms the overlay already uses, and is the
single consumer of saved-view / segment / default-filter expressions.

**Gap it closes:** *saved views*, plus removes a 3× duplicated parser.

### P4 — Permission-gated field/column visibility

**Evidence:** Two *different* gating mechanisms coexist:
`member-form-fields.ts` keys admin-only fields on the overlay's
`visible: false` + a `main.members.admin` permission; `finance-fields.ts`
deliberately does *not* reuse `visible: false` (it would over-restrict
`finance_manager`) and instead carries its own `MEMBERS_FINANCE` permission
constant and a hardcoded `FINANCE_FIELDS_BY_ENTITY` map that mirrors the host's
`policy-read-deny-roles`. The SPA is hand-maintaining a mirror of server policy.

**Extract:** a permission expression on `FieldMetadata` (e.g.
`requiresPermission: "main.members.finance"`) so field/column visibility is
declared once in the overlay and the host's policy and the client's gating are
generated from the same source rather than hand-mirrored.

**Gap it closes:** *permissions*.

### P5 — Workflow-mutation client binding

**Evidence:** `MembershipWorkflowEndpoints.cs` is a clean, documented
convention (sidecar endpoint → `IBifrostWorkflowExecutor` → audit row, with a
pre-flight `PolicyEvaluator` gate). It is correctly *not* generalized on the
server. But the **client** side is ad-hoc: `event-checkin.tsx` hardcodes
`CHECK_IN_ENDPOINT = '/workflows/membership/check-in'`, builds the fetch by
hand, and interprets `409` inline.

**Extract:** a `useWorkflow(endpointPath)` hook (or overlay-declared workflow
descriptors) so calling a workflow endpoint, handling its status codes, and
invalidating affected queries is a primitive, not per-screen plumbing. The
*endpoint bodies* stay app-specific (see bucket 3).

**Gap it closes:** *workflows* (client half).

### P6 — Mobile / touch flow primitives

**Evidence:** `event-checkin.tsx` is the only screen built mobile-first: large
touch-target buttons, a fast text filter over a roster, optimistic inline
feedback. It works, but every bit of it is bespoke — there is no shared
"roster-pick" or "touch list" primitive.

**Extract:** lower priority because there is only **one** instance so far —
extracting from a sample size of one risks the wrong abstraction. Revisit once
a second mobile flow exists. For now, record it as a known gap, not a build
item.

**Gap it closes:** *mobile flows* (deferred — insufficient evidence).

### P7 — Client-side export

**Evidence:** `csv-export.ts` is a clean RFC-4180 serializer that takes
already-fetched, policy-filtered rows + visible columns. It is small and
correct. `export-button.tsx` wires it to the grid.

**Extract:** modest priority — it is small and barely duplicated. Fold into
`@bifrostql/app-shell` as a generic export utility when convenient; not urgent.

## Bucket 3 — Club-specific product logic (do NOT generalize)

These required app code because they *are* the product. The app builder should
make them easy to write — not absorb them.

- **Workflow endpoint bodies** (`MembershipWorkflowEndpoints.cs`) — "record a
  payment" advances the invoice to `paid` and the membership to `active`;
  "renew" advances the term and optionally opens a new invoice; "check-in"
  respects a `UNIQUE(event_id, member_id)` constraint. This orchestration *is*
  the domain. The *convention* (sidecar endpoint + executor + audit) is
  reusable and already documented in `docs/.../guides/workflow-mutations.md`;
  the *bodies* are not.
- **Reports** (`spa/src/reports/`) — "upcoming renewals = `end_date` within 30
  days", "unpaid dues", "attendance by event/member". These are domain
  questions. The *mechanism* (a read-only filtered view over an overlay entity)
  is thin enough that the saved-view engine (P3) largely covers it; the choice
  of *which* reports to show is product.
- **Dashboard** (`spa/src/dashboard/`) — which KPIs a club cares about.
- **Email segments** as a *product feature* — the `emailSegments` overlay key
  and its picker are reusable (and overlap P3's parser), but "active members
  vs. lapsed members" as the segment set is club product.
- **Finance domain rules** — *which* columns are finance-sensitive and *which*
  roles may see them. P4 generalizes the *mechanism*; the policy *values* are
  product.
- **Onboarding / identity-linking flow** (`auth/onboarding.tsx`,
  `link-identity` endpoint) — tied to this app's membership-vs-login model.

## Prioritized extraction roadmap (post-MVP)

| Order | Primitive | Gaps closed | Why this order |
|-------|-----------|-------------|----------------|
| 1 | Relation picker / child-collection editor | relation pickers, nested forms | 3× duplicated, highest boilerplate |
| 2 | Enum options in `FieldMetadata` | validation, saved views | Tiny additive change, unblocks #3 and removes hardcoded vocabularies |
| 3 | Shared filter-expression engine | saved views | 3× duplicated parser, currently drops relative-date views silently |
| 4 | Permission expression on `FieldMetadata` | permissions | Removes hand-mirrored server policy |
| 5 | `useWorkflow` client binding | workflows (client) | Convention is proven server-side; client half is ad-hoc |
| 6 | Mobile / touch primitives | mobile flows | **Deferred** — only one instance; extracting now risks wrong abstraction |
| 7 | Generic CSV export utility | — | Small, low duplication, fold in when convenient |

## Open gap list (carry forward)

- **Relation pickers** — no shared primitive; 3 hand-rolled variants. → P1
- **Nested forms** — child-collection editors mounted in parent forms are
  hand-wired. → P1
- **Validation** — only `email` is declared (`FieldMetadata.validation`); enum
  membership, required, ranges are not expressible. → P2 (enum), needs broader
  validation contract beyond.
- **Saved views** — overlay declares them; client re-parses expressions 3×;
  relative-date views silently dropped. → P3
- **Permissions** — two incompatible gating mechanisms (`visible: false` vs.
  hand-mirrored finance map); client mirrors server policy by hand. → P4
- **Mobile flows** — one bespoke screen, no primitives, insufficient evidence
  to extract. → P6 (deferred)
- **Workflow client binding** — endpoint paths and status handling hardcoded
  per screen. → P5

## Conclusion

The boundary is healthy: everything structural (entities, fields, screens,
nav, host policy) fell out of metadata or shared packages with no app code. The
extraction candidates are all *recurrence* signals — the same shape written 2–3
times — which is exactly the evidence an app-builder primitive should be built
on. Buckets 2 and 3 stay out of the MVP; this roadmap is the post-MVP backlog.
