# Demo-video harness

Generates the narrated demo recordings embedded in the docs. Every clip is the
real application driven by Playwright's bundled Chromium against a seeded
database — nothing is hand-recorded, staged against mock data, or edited by
hand.

Output lands in `docs/public/videos/` as `<scene>.webm` plus a final-frame
`<scene>-poster.png`, and is embedded through
`docs/src/components/DemoVideo.astro`.

## Regenerate

```bash
node docs/videos/capture.mjs              # every scene
node docs/videos/capture.mjs workbench-sql # one or more scene ids
```

The harness builds and starts the apps it needs, drives them, synthesizes the
narration, muxes, writes the posters, and stops the processes it started. A
clean checkout reproduces every committed video from that one command.

Workbench scenes rebuild the frontend (`pnpm --filter @standardbeagle/edit-db
build`, then `pnpm --dir src/BifrostQL.UI/frontend build`) before starting the
host. `src/BifrostQL.UI/wwwroot/assets` is gitignored build output that the
.NET build copies but does not produce, so recording against whatever bundle
happens to be on disk is how you end up with a grid full of `NULL`: an
out-of-date bundle drops every non-key column from the generated query.

### Prereqs

| Need | Default | Override |
|------|---------|----------|
| `ffmpeg` + `ffprobe` on `PATH` | — | — |
| `sqlite3` on `PATH` | — | — |
| Piper TTS | `~/tools/piper` (binary `piper/piper`, voice `en_US-lessac-medium.onnx`) | `PIPER_HOME`, `PIPER_MODEL` |
| Playwright Chromium | `pnpm --dir docs exec playwright install chromium` | — |
| .NET SDK with the `net10.0` targeting pack | — | — |

Ports 5301 (host) and 5310 (workbench) must be free; override with
`BIFROST_DEMO_HOST_PORT` / `BIFROST_DEMO_UI_PORT`.

## How it works

1. **Narration first.** `narration/<scene>.txt` holds one beat per non-comment
   line. Piper synthesizes each beat to a wav, cached by text hash so a re-run
   that only changed the driving script does not re-synthesize.
2. **Capture.** Each scene gets its own browser context recording a 1280×800
   webm. Beat *N* of the script pairs with beat *N* of the scene's `beats`
   array; a count mismatch is a hard error, so a reordered script cannot
   silently narrate the wrong action. Each beat holds until its narration has
   finished speaking.
3. **Align audio to video, not the reverse.** The narration track is assembled
   from the beat start times *actually observed during the recording*. If a UI
   action runs long, the video and the narration shift together instead of
   drifting apart.
4. **Mux and poster.** `ffmpeg -c:v copy -c:a libopus`, then the scene's last
   on-screen frame scaled to 960px wide as the poster.

Working files (seeded database, wavs, raw webm, app logs) go to
`docs/videos/.work/`, which is gitignored. App logs land in
`.work/host.log` and `.work/ui.log` — check them first when a scene fails to
start.

## Scenes

| Scene | App | Shows |
|-------|-----|-------|
| `quickstart` | `BifrostQL.Host` + GraphiQL | Paged query, then one request joining posts → authors, categories, comments |
| `workbench-grid` | `BifrostQL.UI` headless | Schema sidebar, data grid, server-side paging, grouping by column |
| `workbench-pivot` | `BifrostQL.UI` headless | Pivot wells → server-computed cross tabulation → save |
| `workbench-charts` | `BifrostQL.UI` headless | Aggregate chart, chart type, save, dashboard tile, reopen |
| `workbench-sql` | `BifrostQL.UI` headless | Raw SQL console over the desktop bridge |

The `quickstart` scene seeds `blog.db` from `src/BifrostQL.UI/Schemas/blog.sql`
plus `blog-seed-sample.sql` — the same two files the getting-started article
tells a reader to run, so the on-screen responses match the article's. The
workbench scenes use Quick Start's **full** blog dataset (500 posts), so
grouping and pivoting have something to show.

The workbench scenes run Quick Start once off-camera and replay the resulting
session into each scene, so every recording opens straight into the editor
instead of narrating the same 20-second database build five times.

The headless workbench is started with `--enable-http-bridge`. Without it the
SQL console never mounts, because that pane runs over the desktop bridge rather
than the HTTP/GraphQL surface.

## Scenes deliberately omitted

**ER diagram.** The pane cannot load against the current tree. `ErdPane.tsx`
requests `isPolymorphic`, `polymorphicTypeColumn`, and `polymorphicTypeValue`
on `dbJoinSchema`; `MetadataSchemaGenerator.cs` declares all three in the SDL,
but the `singleJoins` projection in
`src/BifrostQL.Core/Resolvers/MetaSchemaResolver.cs` (~lines 146-156) does not
populate them, while the `multiJoins` projection (~lines 130-145) does. The
pane therefore renders `Could not load diagram: Error trying to resolve field
'isPolymorphic'`. This is a product defect, not a harness limitation — add the
scene once the resolver is fixed. (`dbJoinSchema.metadata` is unpopulated in
both projections and will fail the same way if it is ever requested.)

**MCP tools.** The MCP server speaks over stdio, so there is no browser surface
to record and a terminal capture would need a different harness entirely. It is
omitted rather than faked with a staged terminal.

## Adding a scene

1. Write `narration/<id>.txt`, one beat per line, from facts you have verified
   against a running instance — the recordings show the real responses, so a
   wrong number in the script is visible on screen.
2. Add an `<id>` entry to `scenes` in `capture.mjs` with one beat function per
   narration line.
3. `node docs/videos/capture.mjs <id>`, check the poster, then embed with
   `<DemoVideo scene="<id>" title="…" description="…" />`.

Keep the committed total under ~30 MB; the current five scenes are about 13 MB.
