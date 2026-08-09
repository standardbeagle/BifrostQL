#!/bin/bash
# Burns captions onto the raw screen recording and emits the landscape and
# portrait deliverables. Beat offsets come from the capture script's own
# timestamps, shifted by the gap between t0 and the first recorded frame.
set -euo pipefail

# Usage: build-video.sh <raw-recording-dir> [output-dir]
# <raw-recording-dir> is where capture-ui.js wrote its .webm.
RAW="${1:?usage: build-video.sh <raw-recording-dir> [output-dir]}"
SRC="$(ls "$RAW"/*.webm | head -1)"
OUT="${2:-docs/promo}"
FONT=/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf
mkdir -p "$OUT"

# start:end:text — seconds into the recording.
# Landscape sees the full 1920px frame.
CAPTIONS=(
  "0.0:2.0:Point BifrostQL at a SQL database."
  "2.0:10.3:Pick a schema. No setup, no config files."
  "10.3:18.8:The schema is read at startup - 7 tables, 4,825 rows."
  "18.8:24.6:Every foreign key resolves to a label - even two to the same table."
  "24.6:29.5:Related-row counts per row, from that same schema read."
  "29.5:33.8:Drill into related rows without writing a query."
  "33.8:42.6:Edit forms generated from the schema, with FK pickers."
  "42.6:57.6:And the whole GraphQL API - generated. Nested joins included."
)

# Portrait crops to the left of the frame, so its captions describe what is
# actually visible there — the 1920px-wide grid's right-hand columns (the
# resolved foreign key) are outside the 4:5 crop.
PORTRAIT_CAPTIONS=(
  "0.0:2.0:Point BifrostQL at a SQL database."
  "2.0:10.3:Pick a schema. No setup, no config files."
  "10.3:18.8:Schema read at startup - 7 tables, 4,825 rows."
  "18.8:24.6:800 orders, every foreign key resolved to a label."
  "24.6:29.5:500 products, browsable and paged."
  "29.5:33.8:Drill into related rows without writing a query."
  "33.8:42.6:Edit forms generated from the schema."
  "42.6:57.6:And the whole GraphQL API - generated."
)

build_filter() {
  local -n caps=$1
  local size=$2
  local ypos=$3
  local out=""
  for c in "${caps[@]}"; do
    local start="${c%%:*}"; local rest="${c#*:}"
    local end="${rest%%:*}"; local text="${rest#*:}"
    # Captions deliberately contain no apostrophes or colons, so they need no
    # escaping inside the drawtext single-quoted argument.
    out+="drawtext=fontfile=${FONT}:text='${text}':fontcolor=white:fontsize=${size}"
    out+=":box=1:boxcolor=0x0f172a@0.88:boxborderw=22"
    out+=":x=(w-text_w)/2:y=h-${ypos}"
    out+=":enable='between(t,${start},${end})',"
  done
  printf '%s' "${out%,}"
}

filter="$(build_filter CAPTIONS 40 160)"
pcaptions="$(build_filter PORTRAIT_CAPTIONS 27 190)"

echo "== landscape 1920x1080 =="
ffmpeg -v error -y -i "$SRC" -vf "${filter}" \
  -c:v libx264 -pix_fmt yuv420p -crf 20 -preset medium -movflags +faststart -an \
  "$OUT/bifrostql-demo-1920x1080.mp4"

# Portrait: crop the 16:9 frame straight to 4:5 (864x1080) and scale up, so the
# frame is filled edge to edge rather than letterboxed. The crop is biased left
# because the app is left-anchored - the sidebar and the identifying columns are
# what a feed viewer needs to see.
echo "== portrait 1080x1350 =="
ffmpeg -v error -y -i "$SRC" -vf "crop=864:1080:150:0,scale=1080:1350,${pcaptions}" \
  -c:v libx264 -pix_fmt yuv420p -crf 20 -preset medium -movflags +faststart -an \
  "$OUT/bifrostql-demo-1080x1350.mp4"

echo "== done =="
ls -la "$OUT"/*.mp4
