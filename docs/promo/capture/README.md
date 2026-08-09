# Promo capture tooling

The scripts that produced the videos in `docs/promo`. They are here so the
footage can be regenerated rather than trusted — every claim in
`../social-copy.md` is meant to be re-checkable against a running stack.

| File | What it does |
|---|---|
| `capture-ui.js` | Drives the live BifrostQL UI through the demo beats and records 1920x1080. Returns each beat's end offset in ms, which is where `build-video.sh`'s caption timings come from. |
| `build-video.sh` | Burns captions onto the raw recording and emits the landscape and portrait cuts. |
| `protocol-transcript.sh` | Runs the real multi-protocol session and writes the verbatim transcript. |
| `capture-protocols.js` | Records the rendered transcript page. |

## Recording the product demo

Start the UI host with a database loaded (the video uses Quick Start's
e-commerce schema, full dataset), then run `capture-ui.js` through a Playwright
runner with `BIFROST_CAPTURE_DIR` pointing at an empty directory:

```bash
BIFROST_CAPTURE_DIR=/tmp/bifrost-capture  # capture-ui.js writes <dir>/*.webm
bash docs/promo/capture/build-video.sh /tmp/bifrost-capture docs/promo
```

If you change the beat timings in `capture-ui.js`, take the `atMs` values it
returns and update the `CAPTIONS` / `PORTRAIT_CAPTIONS` arrays in
`build-video.sh` — the caption windows are offsets into the recording, and the
script does not derive them.

The two caption sets are deliberately different. The portrait cut crops to a 4:5
slice of the left of the frame, so a caption describing the grid's right-hand
columns would be describing something the viewer cannot see.

## Recording the protocol demo

`protocol-transcript.sh` expects a host exposing GraphQL (:5099), pgwire
(:55432), RESP (:6399) and OData over one database — see
`docs/src/content/docs/guides/protocol-adapters.md` for the wiring. It masks the
demo password in the echoed commands but not in what it actually sends, so run
it against a local rig only.

Render the resulting transcript into a page and record it with
`capture-protocols.js` (`BIFROST_TRANSCRIPT_PAGE` points at the page).

## Honesty rules for this footage

- The only alteration to the product demo is a CSS rule hiding the welcome
  screen's saved-connection list, which holds real customer hostnames.
- The protocol video re-paces the reveal for legibility. Commands and output are
  verbatim; `protocol-transcript.txt` is committed next to the video so the two
  can be compared.
- Before reusing a caption, re-read `../social-copy.md`'s "Do not claim"
  section. At least one caption there was moved off the Orders grid because the
  frame visibly contradicted it.
