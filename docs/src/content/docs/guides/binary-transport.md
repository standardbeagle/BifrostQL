---
title: "Binary Transport Guide"
description: "Connect to BifrostQL over a protobuf-over-WebSocket endpoint with chunked streaming, automatic resume across transient disconnects, and TypeScript codegen from a .proto schema."
---

BifrostQL ships an alternative WebSocket transport that exchanges GraphQL queries and responses as protobuf-encoded binary frames instead of JSON over HTTP. It is roughly 60% smaller on the wire for binary or large payloads, supports async-iterator streaming so consumers can process chunks as they arrive, and automatically resumes interrupted transfers across transient network drops.

## What it is

The binary transport is a single WebSocket endpoint that multiplexes many in-flight GraphQL requests over one connection. Each request is a protobuf frame carrying a `request_id`, the query text, and any variables; responses come back as either a single `Result` frame or as a sequence of CRC32-checksummed `Chunk` frames that the client reassembles. Reconnects are transparent — the client snapshots the highest contiguous chunk sequence per request and resumes from where the server left off.

## When to use it

| Situation | JSON / HTTP | Binary / WebSocket |
|---|---|---|
| Small, interactive queries from a page load | Best — request/response, easy to cache | Overkill, adds connection setup |
| File downloads, generated reports, image blobs | Slow, base64 inflation | Best — streaming, no base64 |
| Long-lived clients running many queries (desktop, mobile, services) | Reconnect every request | Best — one connection, multiplexed |
| Networks with frequent transient drops | Each request fails on its own | Best — automatic resume per request |
| Public APIs consumed by curl or a generic GraphQL client | Best — universal tooling | Requires the binary client |

If your workload is small interactive queries from a browser tab, stay on JSON. If you ship images, generated PDFs, large result sets, or you run a long-lived process that talks to BifrostQL constantly, the binary transport is the right choice.

## Server setup

Register the WebSocket binary endpoint with `UseBifrostBinary()` after `UseWebSockets()` and after the BifrostQL engine has been added to DI. The default path is `/bifrost-ws`.

```csharp
using BifrostQL.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddBifrostQL(o => o
    .BindStandardConfig(builder.Configuration));

var app = builder.Build();

app.UseWebSockets();
app.UseBifrostBinary();   // mounts WebSocket endpoint at /bifrost-ws
app.UseBifrostQL();        // standard JSON endpoint stays available

app.Run();
```

`UseBifrostBinary` accepts three optional parameters:

```csharp
app.UseBifrostBinary(
    path: "/bifrost-ws",   // WebSocket endpoint path
    chunkThreshold: 64 * 1024, // payloads above this are chunked (default 64 KB)
    ackWindow: 8);          // max unacked chunks before backpressure (default 8)
```

The JSON `/graphql` endpoint and the binary `/bifrost-ws` endpoint can run side-by-side on the same server. Clients pick whichever transport fits the request.

## Client install

The TypeScript client is published as `@bifrostql/binary-client`:

```bash
npm install @bifrostql/binary-client
```

Connect, run two queries concurrently over the same socket, and close:

```ts
import { BifrostBinaryClient } from "@bifrostql/binary-client";

const client = new BifrostBinaryClient({
  url: "ws://localhost:5000/bifrost-ws",
  requestTimeoutMs: 10_000,
  onOpen: () => console.log("Connected"),
  onClose: (code, reason) => console.log(`Disconnected: ${code} ${reason}`),
  onError: (err) => console.error("Error:", err),
});

await client.connect();

const [users, orders] = await Promise.all([
  client.query("{ users { id name email } }"),
  client.query("{ orders { id total status } }"),
]);

console.log("Users:", users.data);
console.log("Orders:", orders.data);

client.close();
```

Mutations use the same client with variables:

```ts
const result = await client.mutate(
  `mutation ($input: usersInput!) {
    insert_users(input: $input) { id name }
  }`,
  { input: { name: "Alice", email: "alice@example.com" } }
);

if (result.errors.length > 0) {
  console.error("Mutation errors:", result.errors);
} else {
  console.log("Inserted:", result.data);
}
```

In Node.js environments without a global `WebSocket`, pass a constructor explicitly:

```ts
import WebSocket from "ws";
const client = new BifrostBinaryClient({
  url: "ws://localhost:5000/bifrost-ws",
  WebSocket,
});
```

## Streaming

For large responses (downloads, generated reports, image blobs) call `client.stream()` instead of `client.query()`. It returns an `AsyncIterableIterator<StreamChunk>` that yields each chunk as soon as it arrives and is verified, so you can write bytes to disk or pipe them to a consumer without waiting for the full transfer to reassemble in memory.

```ts
for await (const chunk of client.stream("{ download_large_blob }")) {
  console.log(`chunk ${chunk.sequence + 1}/${chunk.totalChunks} (${chunk.bytes.length} bytes)`);
  if (chunk.isLast) console.log("download complete");
}
```

Each `StreamChunk` carries:

| Field | Description |
|---|---|
| `requestId` | The request id of the originating query. |
| `sequence` | 0-based chunk index, always emitted in ascending order. |
| `totalChunks` | Total number of chunks in this transfer. |
| `bytes` | Raw payload bytes for this chunk. |
| `isLast` | True only when `sequence === totalChunks - 1`. |

Chunks arrive in sequence even if the wire delivered them out of order — the client buffers and reorders internally. Mutations have a streaming counterpart, `client.streamMutation()`, with the same shape.

## Auto-resume

When the WebSocket drops mid-transfer, the client snapshots the highest contiguous chunk sequence it has received for each in-flight request, opens a fresh socket using an exponential backoff (100 ms → 30 s with 25% jitter), and sends a `Resume` frame asking the server to retransmit only the chunks that are still missing. CRC32 verification on every chunk means partial retransmits are safe — bytes that already arrived intact are kept, and the tail is appended once it shows up.

Reconnect behavior is controlled through `BifrostClientOptions`:

```ts
const client = new BifrostBinaryClient({
  url: "ws://localhost:5000/bifrost-ws",
  autoReconnect: true,            // default
  maxReconnectAttempts: 10,       // default Infinity
  onReconnect: (attempt) => console.log(`Reconnected on attempt ${attempt}`),
  onReconnectFailed: (attempts, err) =>
    console.error(`Gave up after ${attempts} attempts: ${err.message}`),
});
```

A normal close (code 1000) or an explicit `client.close()` call never triggers reconnect. When reconnects are exhausted, all pending requests reject with the last connect error and `onReconnectFailed` fires.

## Codegen

`@bifrostql/codegen` reads a BifrostQL `.proto` schema and emits typed TypeScript interfaces — one file per message plus a barrel `index.ts`. Run it as a one-shot CLI from a checked-in proto file:

```bash
npx @bifrostql/codegen --proto-file ./schema/bifrost.proto --out ./generated
```

CLI flags:

| Flag | Description |
|---|---|
| `--proto-file <path>` | Path to a local `.proto` file. |
| `--out <dir>` | Output directory for generated `.ts` files (default `./generated`). |
| `--header <key=value>` | HTTP header to send on the WebSocket handshake (repeatable). |
| `-h`, `--help` | Show help and exit. |

A `--endpoint <ws-url>` flag is pre-wired for fetching the `.proto` text directly from a running server, but the server does not yet expose its proto schema as a GraphQL field. Until that lands, prefer `--proto-file` against a checked-in copy of the schema (the .NET `ProtoSchemaGeneratorTests` test can capture one for you).

## Troubleshooting

### Can I mix binary and JSON transports?

Yes. `UseBifrostBinary()` and `UseBifrostQL()` register independent middleware. The same server can serve both, and your clients are free to pick the right transport per request. They share the same schema and the same module pipeline.

### What happens if the server is down when I call `connect()`?

`connect()` rejects with the connect error on the very first attempt. After a successful connect, if the server later drops the socket, the client enters the auto-reconnect cycle and pending requests stay queued until the socket is back (or until `maxReconnectAttempts` is hit, at which point they reject with the last error).

### How do I force a fresh connection?

Call `client.close()` to drop the current socket, error any active streams with a "client closed" error, and stop the reconnect controller. Then construct a new `BifrostBinaryClient` and call `connect()` on it. The previous instance is permanently closed and cannot be reopened.

### How big are chunks?

The server defaults to a 64 KB chunk threshold and an 8-chunk ack window — payloads above 64 KB are chunked, and the server holds back at 8 unacked chunks to apply backpressure. Both values are tunable through the parameters of `UseBifrostBinary()`.
