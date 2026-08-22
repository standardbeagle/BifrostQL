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
| `workbench-visualize` | `BifrostQL.UI` headless | Grid filter → Visualize → chart builder pre-populated with the same filter → save |
| `workbench-sankey` | `BifrostQL.UI` headless | Sankey flow between two categorical dimensions (e-commerce dataset: searched vs purchased category) |
| `workbench-sankey-dashboard` | `BifrostQL.UI` headless | Measure switch (count → sum of revenue), dashboard tile bound to the saved chart, band click drills to the filtered grid |
| `workbench-json` | `BifrostQL.UI` headless | JSON documents in the grid: hover preview, side panel, full screen, Format, save (IoT dataset) |
| `workbench-attachments` | `BifrostQL.UI` headless | Images and PDFs stored as blobs: inline preview, row navigation, Download (IoT dataset) |
| `workbench-sql` | `BifrostQL.UI` headless | Raw SQL console over the desktop bridge |
| `workbench-erd` | `BifrostQL.UI` headless | ER diagram: foreign keys, collapsed junction, column expand, N-hop filter, click-to-open |
| `mcp-tools` | `BifrostQL.Host` + MCP HTTP | Live MCP session in a rendered console: initialize, tools/list, real query/aggregate/search calls |
| `mcp-opencode` | `BifrostQL.Host` + MCP HTTP + OpenCode | A real coding agent (DeepSeek v4 Flash via OpenRouter) answers a data question through the bifrost tools; closes with an on-camera sqlite verification (needs OPENROUTER_KEY) |
| `chat-connectors` | `BifrostQL.Host` (ChatDemo) + chat SPA | Chat over the database: explore tool chips, media grid from blobs, plan proposal approved (needs an API key, below) |

The `quickstart` scene seeds `blog.db` from `src/BifrostQL.UI/Schemas/blog.sql`
plus `blog-seed-sample.sql` — the same two files the getting-started article
tells a reader to run, so the on-screen responses match the article's. The
workbench scenes use Quick Start's **full** blog dataset (500 posts), so
grouping and pivoting have something to show.

The workbench scenes run Quick Start once off-camera PER DATASET and replay the
resulting session into each scene, so every recording opens straight into the
editor instead of narrating the same 20-second database build five times. Most
scenes share the blog session (`ui: true`); a scene that names a dataset
(`ui: 'ecommerce'`, the sankey scene) gets its own off-camera Quick Start.

The headless workbench is started with `--enable-http-bridge`. Without it the
SQL console never mounts, because that pane runs over the desktop bridge rather
than the HTTP/GraphQL surface.

## Scenes deliberately omitted

**MCP tools** used to be listed here (stdio had no browser surface, and a staged
terminal would have been fake). The Streamable HTTP transport now gives a
driveable surface: the `mcp-tools` scene renders a console page and performs
the REAL JSON-RPC session against the running host while recording — every
displayed response is the live server's answer, so the no-staging rule holds.

**The chat scene calls a real model.** `chat-connectors` refuses to run without
a key: `ANTHROPIC_API_KEY` (direct Anthropic), or `OPENROUTER_KEY` (routed
through OpenRouter's Anthropic-compatible endpoint via the chat module's
`BaseUrl` override). Keys come from the environment or a git-ignored repo-root
`.env.local`; they flow into child process environments only and are never
logged. Its
narration deliberately never quotes model output — it describes the mechanics
(tool chips, the media route, the proposal card), which are on screen whatever
the model happens to say.

The ER diagram used to be listed here: the pane could not load because the
`singleJoins` projection in `src/BifrostQL.Core/Resolvers/MetaSchemaResolver.cs`
did not populate the `isPolymorphic`, `polymorphicTypeColumn`, and
`polymorphicTypeValue` fields that the SDL declares on `dbJoinSchema` and that
`ErdPane.tsx` selects, so the pane rendered `Could not load diagram: Error
trying to resolve field 'isPolymorphic'`. That defect is fixed and the scene is
recorded. (`dbJoinSchema.metadata` is still unpopulated in both projections and
will fail the same way if it is ever requested.)

## Adding a scene

1. Write `narration/<id>.txt`, one beat per line, from facts you have verified
   against a running instance — the recordings show the real responses, so a
   wrong number in the script is visible on screen.
2. Add an `<id>` entry to `scenes` in `capture.mjs` with one beat function per
   narration line.
3. `node docs/videos/capture.mjs <id>`, check the poster, then embed with
   `<DemoVideo scene="<id>" title="…" description="…" />`.

Keep the committed total under ~30 MB; the current six scenes are about 17 MB.
