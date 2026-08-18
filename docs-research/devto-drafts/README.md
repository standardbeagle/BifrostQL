# dev.to draft packet

Ten publish-ready drafts. Each file carries dev.to frontmatter (`published:
false`, `canonical_url` to the docs site, ≤4 tags) and pastes straight into
the dev.to editor. All shown output was captured from real runs; steps that
were not executed are marked as such inside each article.

## Publish order (weekly cadence)

| Week | File | Series |
|------|------|--------|
| 1 | `01-graphql-api-in-5-minutes.md` | BifrostQL quickstarts |
| 2 | `02-connect-your-database.md` | BifrostQL quickstarts |
| 3 | `04-eight-app-schemas.md` | BifrostQL quickstarts |
| 4 | `03-wordpress-graphql.md` | — |
| 5 | `05-postgres-wire-protocol.md` | — |
| 6 | `06-database-as-mcp-tools.md` | — |
| 7 | `07-field-encryption-key-rotation.md` | — |
| 8 | `08-approval-workflows.md` | — |
| 9 | `09-rss-feeds-from-tables.md` | — |
| 10 | `10-data-retention-erasure.md` | — |

Quickstarts lead because they serve the broadest search intent; the roundup
(week 3) links back to both and closes the series arc. Feature articles
follow in adoption order: BI tooling first, agent tooling second, then the
compliance trio.

## Before publishing each article

1. Set `published: true` (or leave false and use the dev.to preview).
2. Add a `cover_image` — poster stills come from the demo-video phase
   (plan: `docs-research/plans/content-marketing-plan-2026-08.md`, Phase 3);
   dev.to renders covers at 1000×420.
3. Confirm the `canonical_url` page is live on the docs site first, so
   search engines index the canonical before the syndicated copy.

## Grounding evidence

Capture details (versions, ports, dates) are recorded in each article's
"how this was run" section. The grounding runs also surfaced findings that
belong to the repo, not the articles — see the follow-ups noted in the
content plan and, for the approval-hook serialization defect, the task
tracker.
