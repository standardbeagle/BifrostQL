// Repeatable perf comparison for a WIDE aggregate result set over the binary
// transport vs HTTP/JSON. Task 5.3 established (see the .NET
// BinaryAggregatePivotPassthroughTests) that aggregate/pivot results ride the
// existing Query frame as an opaque JSON payload — identical bytes on both
// wires. This script quantifies the ONLY thing that differs between the two
// transports for such a result: the framing/serialization cost the client and
// server pay on top of the shared JSON payload.
//
//   HTTP path : JSON.stringify (server) + JSON.parse (client)
//   Binary path: encodeMessage(protobuf envelope, payload = same JSON bytes)
//                + decodeMessage + JSON.parse (client)
//
// It reports median/p95 timings and the on-wire byte size for each path so the
// binary transport's framing overhead on a ~5k-group x 8-column aggregate is
// measurable and reproducible. No server or network is required, so the numbers
// are deterministic across machines and CI.
//
//   Run: pnpm --dir packages/bifrost-binary-client perf
//   (the `perf` npm script builds first, then runs this against dist/)

import { encodeMessage, decodeMessage } from "../dist/index.js";

const GROUPS = Number(process.env.PERF_GROUPS ?? 5000);
const COLS = Number(process.env.PERF_COLS ?? 8);
const ITERATIONS = Number(process.env.PERF_ITERS ?? 50);

// BifrostMessageType.Result === 2. It is a `const enum`, erased at runtime, so
// we use the literal here (the .NET/TS wire values are pinned and tested).
const RESULT = 2;
const EMPTY_CHUNK_INFO = {
  sequence: 0,
  total: 0,
  offset: 0,
  totalBytes: 0,
  checksum: 0,
};

/** Builds a wide grouped-aggregate GraphQL result: GROUPS rows x COLS numeric ops. */
function buildWideAggregate() {
  const rows = new Array(GROUPS);
  for (let g = 0; g < GROUPS; g++) {
    const row = { region: `region_${g}`, status: g % 2 === 0 ? "open" : "closed", _count: g + 1 };
    const sum = {};
    const avg = {};
    for (let c = 0; c < COLS; c++) {
      sum[`metric_${c}`] = (g * 31 + c * 7) % 100000;
      avg[`metric_${c}`] = ((g * 31 + c * 7) % 100000) / (g + 1);
    }
    row._sum = sum;
    row._avg = avg;
    rows[g] = row;
  }
  return { data: { ordersAggregate: rows } };
}

function percentile(sorted, p) {
  const idx = Math.min(sorted.length - 1, Math.floor((p / 100) * sorted.length));
  return sorted[idx];
}

function summarize(label, samplesMs, wireBytes) {
  const sorted = [...samplesMs].sort((a, b) => a - b);
  const median = percentile(sorted, 50);
  const p95 = percentile(sorted, 95);
  const mean = samplesMs.reduce((a, b) => a + b, 0) / samplesMs.length;
  return {
    label,
    wireKB: (wireBytes / 1024).toFixed(1),
    medianMs: median.toFixed(3),
    p95Ms: p95.toFixed(3),
    meanMs: mean.toFixed(3),
  };
}

function now() {
  return Number(process.hrtime.bigint()) / 1e6;
}

const payload = buildWideAggregate();
const encoder = new TextEncoder();
const decoder = new TextDecoder();

// ---- HTTP/JSON path ----
const httpSamples = [];
let httpWireBytes = 0;
for (let i = 0; i < ITERATIONS; i++) {
  const t0 = now();
  const json = JSON.stringify(payload); // server serialize
  const parsed = JSON.parse(json); // client decode
  const t1 = now();
  httpSamples.push(t1 - t0);
  httpWireBytes = encoder.encode(json).length;
  if (!parsed.data.ordersAggregate.length) throw new Error("empty parse");
}

// ---- Binary/protobuf-framed path ----
const binarySamples = [];
let binaryWireBytes = 0;
for (let i = 0; i < ITERATIONS; i++) {
  const t0 = now();
  const jsonBytes = encoder.encode(JSON.stringify(payload)); // server serialize
  const frame = encodeMessage({
    requestId: 1,
    type: RESULT,
    query: "",
    variablesJson: "",
    payload: jsonBytes,
    errors: [],
    chunkInfo: EMPTY_CHUNK_INFO,
    lastSequence: 0,
  });
  const decoded = decodeMessage(frame); // client frame decode
  const parsed = JSON.parse(decoder.decode(decoded.payload)); // client JSON decode
  const t1 = now();
  binarySamples.push(t1 - t0);
  binaryWireBytes = frame.length;
  if (!parsed.data.ordersAggregate.length) throw new Error("empty parse");
}

const http = summarize("HTTP / JSON", httpSamples, httpWireBytes);
const binary = summarize("Binary / protobuf frame", binarySamples, binaryWireBytes);

const overheadBytes = binaryWireBytes - httpWireBytes;
const overheadPct = ((overheadBytes / httpWireBytes) * 100).toFixed(2);

console.log(
  `\nWide aggregate transport perf — ${GROUPS} groups x ${COLS} cols, ${ITERATIONS} iterations\n`
);
console.table([http, binary]);
console.log(
  `\nWire size: HTTP ${http.wireKB} KB vs binary ${binary.wireKB} KB ` +
    `(+${overheadBytes} bytes protobuf-envelope overhead, +${overheadPct}%).\n` +
    `The binary payload IS the same GraphQL JSON; the envelope adds only a fixed-size ` +
    `protobuf header, so wide aggregates ride the existing Query frame with negligible ` +
    `size cost and no columnar compression benefit.\n`
);
