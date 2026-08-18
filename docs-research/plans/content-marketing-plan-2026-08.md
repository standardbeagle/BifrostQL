# BifrostQL Content & SEO Plan — docs site, dev.to, demo videos

Status: PLAN (2026-08-17). Owner: Andy. Executor: Claude sessions using
`standardbeagle-marketing` plugin skills (`dev-article`, `seo-optimizer`,
`content-repurposer`) — plugin lives at
`/home/beagle/work/marketplace/plugins/standardbeagle-marketing` and must be
enabled in the session that executes this plan.

Decisions already made:
- **dev.to: draft-only.** Every dev.to article is delivered as a markdown file
  with dev.to frontmatter (`published: false`, `canonical_url` pointing at the
  docs site). Andy pastes/publishes. No API key in devkey.
- **Videos: automated**, agnt-style — Playwright drives the real app, records
  webm per scene; narration comes from a locally pulled TTS model and is muxed
  with ffmpeg. Videos are self-hosted in the docs site (webm + poster still),
  the same `<ModeVideo>`-style pattern agnt uses. Upload to an external video
  host (YouTube) is out of scope until an account/credential is provided.
- **Grounding rule (dev-article skill) is binding**: every command, flag, and
  output in an article must be run for real first. No invented output.

## Phase 0 — Ground-truth inventory (1 session)

1. Feature census from `git log origin/main` + AGENTS.md: build
   `docs-research/plans/feature-matrix.md` mapping every shipped feature →
   existing docs page(s) → gap. Known gaps as of the 154-commit push:
   - **LDAP adapter has no guide** (`guides/ldap.md` missing; epic still in
     progress — write the guide when simple-bind lands fully).
   - Key-rotation content: verify where `docs(crypto): add key-rotation guide`
     landed and that it covers the DEK-version + online re-encryption sweep.
   - SqlExpr public builder, computed Expression columns, retention/erasure,
     approval + deferred change sets, feeds, workbench panes — pages exist;
     verify they match shipped behavior (docs-authority rule: canonical docs
     must be verified against source).
2. Schema census: the 9 out-of-the-box app schemas in
   `src/BifrostQL.UI/Schemas/` (blog, classroom, crm, ecommerce,
   membership-manager, org-model, project-tracker, sqlite-advanced, plus
   `crm.bifrost.json` app-metadata) — record for each: tables, seed variants
   (sample/full), which dialects have seeds (org-model + membership-manager
   have postgres seeds).
3. Verify each schema actually loads: `./bifrostui` against each seeded SQLite
   file; capture the working connection string and one working query per
   schema. These become the grounded openings of the quickstarts.

## Phase 1 — Docs-site SEO pass (seo-optimizer skill)

Target: every page in `docs/src/content/docs` (~80 pages).

1. **Technical baseline** (one PR):
   - Astro/Starlight `head` config: default meta description, OpenGraph/Twitter
     cards, canonical URLs, `@astrojs/sitemap` + robots.txt if absent.
   - JSON-LD: `TechArticle` on guides, `HowTo` on quickstarts, `SoftwareApplication`
     on the landing page.
2. **Per-page pass** (batched PRs, seo-optimizer output format per page):
   - Frontmatter `title` (50–60 chars, keyword-first) and `description`
     (150–160 chars) on every page — audit shows many pages rely on defaults.
   - One H1 per page, keyword in first 100 words, heading hierarchy.
   - Internal-link mesh: every concept page links to its guide and vice versa;
     quickstarts link to the schema reference and the workbench guides.
   - Keyword map (primary/secondary per page) recorded in
     `docs-research/plans/keyword-map.md` so pages don't cannibalize each other.
     Anchor terms: "GraphQL API for SQL Server", "database to GraphQL",
     "no-code GraphQL backend", "Postgres wire protocol emulation",
     "database MCP server", per-schema terms ("CRM database schema GraphQL").
3. **Gap pages** from Phase 0 written fresh (LDAP guide when ready, any
   missing feature page).

Acceptance: sitemap builds; every page has unique title+description; internal
links resolve (`pnpm --dir docs build` green).

## Phase 2 — Quickstart & how-to articles (dev-article skill)

Written twice: canonical version on the docs site, dev.to adaptation
(draft-only, `canonical_url` → docs site). Priority order:

| # | Article | Docs slug | dev.to angle |
|---|---------|-----------|--------------|
| 1 | GraphQL API from any SQL database in 5 minutes | `getting-started/` rewrite | launch-style walkthrough, SQLite → workbench |
| 2 | Connecting your database (SQL Server, Postgres, MySQL, SQLite conn strings + auth) | `getting-started/connect-a-database` (new) | "one connection string, full API" |
| 3 | Query WordPress data over GraphQL | polish `guides/wordpress.md` | "your WP database is already an API" |
| 4 | Blog schema quickstart | `getting-started/app-schemas/blog` (new) | build a headless blog backend |
| 5 | CRM schema quickstart (incl. `crm.bifrost.json` app-metadata forms/grids) | `.../crm` | CRM backend + admin UI, zero code |
| 6 | E-commerce schema quickstart | `.../ecommerce` | storefront data layer |
| 7 | Project-tracker schema quickstart | `.../project-tracker` | internal tools |
| 8 | Classroom, membership-manager, org-model, sqlite-advanced quickstarts | one page each | batch as a "9 ready-made schemas" roundup post on dev.to |
| 9 | Point Grafana/Metabase at BifrostQL (pgwire) | polish `guides/pgwire-bi-smoke.md` | "Postgres wire protocol without Postgres" |
| 10 | Your database as MCP agent tools | polish `guides/mcp-server.md` | AI-agent audience |
| 11 | Field encryption + DEK rotation | extend `guides/field-encryption.md` | security audience |
| 12 | Approval workflows / deferred change sets | polish `guides/approval-workflows.md` + `guides/deferred-effects.md` | "maker-checker for row edits" |
| 13 | RSS/Atom feeds from tables | polish `guides/feeds.md` | syndication niche |
| 14 | Retention & right-to-erasure | polish `guides/retention.md` | GDPR audience |

Rules per article (from dev-article SKILL.md — read it in the executing
session): grounding (run everything), anti-slop list, banned constructions,
open with the concrete payoff, end with actionable takeaways, dev.to
frontmatter with `published: false`. SEO pass (Phase 1 format) on each before
merge. dev.to drafts land in `docs-research/devto-drafts/NN-slug.md`.

## Phase 3 — Demo videos (agnt-pattern harness)

Adapt `agnt/docs-site/screenshots/` (see its README) to BifrostQL:

1. **Harness** `docs/videos/capture.mjs`: Playwright bundled Chromium →
   `src/BifrostQL.Host` running a seeded SQLite schema (one process per
   scene, `scripts/` seed first). Scenes scripted against the real workbench
   UI: connect → schema browse → query → grid grouping → pivot → chart →
   dashboard → ERD, plus one scene per quickstart article. Output: per-scene
   `webm` + final-frame PNG poster (`ffmpeg` scale per agnt README).
2. **Narration**: per-scene narration script written alongside the article
   (same grounded facts). Pull a local TTS model — Piper
   (`rhasspy/piper`, ONNX voices, CPU-only, no cloud key) as default;
   Deepgram Aura only if Andy later grants a key via devkey. Pipeline:
   narration.txt → wav per scene → `ffmpeg -i scene.webm -i scene.wav -c:v copy
   -c:a libopus scene-narrated.webm`. Scene timing driven by the narration
   durations (Playwright waits keyed to audio-segment lengths).
3. **Hosting**: committed under docs `static/`, embedded on the matching docs
   page with poster + `prefers-reduced-motion` fallback (port agnt's
   `ModeVideo` component). External upload (YouTube) deferred — needs Andy's
   account; when granted, titles/descriptions come from the keyword map.

Acceptance: `node docs/videos/capture.mjs` reproduces every video from a clean
checkout; no hand-recorded footage.

## Phase 4 — dev.to publication packet

- `docs-research/devto-drafts/README.md`: publish order (weekly cadence,
  roundup post last), per-article tags (max 4, from dev.to tag list), series
  name ("BifrostQL quickstarts"), cover-image spec (poster stills from Phase 3
  scenes, 1000×420 crop).
- Each draft: frontmatter `title`, `published: false`, `tags`,
  `canonical_url`, `series`, `cover_image` path; body already
  liquid-tag-safe. Andy pastes into the dev.to editor or wires an API key
  later.

## Sequencing & effort

Phase 0 → 1 → 2 are strictly ordered (SEO pass needs the inventory; articles
need the SEO keyword map). Phase 3 starts after the first 3 articles exist
(scenes mirror articles). Phase 4 is a closer per article batch.
Suggested loop: load this plan into worktrack as an epic with one slice per
phase-1 batch / article / scene-group, using the standard slice workflow.

## Out of scope / blocked

- dev.to API publishing (no key; draft-only by decision).
- YouTube or other video-host upload (no account/credential configured).
- LDAP guide before the LDAP epic closes.
