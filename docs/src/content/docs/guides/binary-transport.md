---
title: "Protobuf over WebSocket Transport"
description: "Stream large result sets over a protobuf WebSocket endpoint with chunked delivery, automatic reconnect after transient disconnects, and TypeScript codegen."
---

BifrostQL ships an alternative WebSocket transport that exchanges GraphQL queries and responses as protobuf-encoded binary frames instead of JSON over HTTP. It is roughly 60% smaller on the wire for binary or large payloads, supports async-iterator streaming so consumers can process chunks as they arrive, and reconnects automatically after transient network drops.

## What it is

The binary transport is a single WebSocket endpoint that multiplexes many in-flight GraphQL requests over one connection. Each request is a protobuf frame carrying a `request_id`, the query text, and any variables; responses come back as either a single `Result` frame or as a sequence of CRC32-checksummed `Chunk` frames that the client reassembles. After a dropped connection the client reconnects on its own and re-sends in-flight queries; see [Auto-reconnect](#auto-reconnect) for what happens to mutations and streams.

## When to use it

| Situation | JSON / HTTP | Binary / WebSocket |
|---|---|---|
| Small, interactive queries from a page load | Best — request/response, easy to cache | Overkill, adds connection setup |
| File downloads, generated reports, image blobs | Slow, base64 inflation | Best — streaming, no base64 |
| Long-lived clients running many queries (desktop, mobile, services) | Reconnect every request | Best — one connection, multiplexed |
| Networks with frequent transient drops | Each request fails on its own | Automatic reconnect; queries are re-sent, mutations and started streams fail |
| Public APIs consumed by curl or a generic GraphQL client | Best — universal tooling | Requires the binary client |

If your workload is small interactive queries from a browser tab, stay on JSON. If you ship images, generated PDFs, large result sets, or you run a long-lived process that talks to BifrostQL constantly, the binary transport is the right choice.

## Server setup

Register the WebSocket binary endpoint with `UseBifrostBinary()` after `UseWebSockets()`, after the BifrostQL engine has been added to DI, and after the middleware that authenticates requests (`UseBifrostQL()` / `UseBifrostEndpoints()` add `UseAuthentication` when auth is on). The mount's identity gate reads the principal the authentication middleware populates; mounted ahead of it, the gate sees every caller as anonymous and closes every connection on an auth-required endpoint. The default path is `/bifrost-ws`.

```csharp
using BifrostQL.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddBifrostQL(o => o
    .BindStandardConfig(builder.Configuration));

var app = builder.Build();

app.UseWebSockets();
app.UseBifrostQL();        // standard JSON endpoint; adds UseAuthentication when auth is on
app.UseBifrostBinary();   // mounts WebSocket endpoint at /bifrost-ws, after authentication

app.Run();
```

`UseBifrostBinary` accepts these optional parameters:

```csharp
app.UseBifrostBinary(
    path: "/bifrost-ws",       // WebSocket endpoint path
    chunkThreshold: 64 * 1024, // payloads above this are chunked (default 64 KB)
    ackWindow: 8,              // max unacked chunks before backpressure (default 8)
    maxConnections: 100,       // concurrent sockets on this mount
    firstFrameTimeout: TimeSpan.FromSeconds(30),
    idleTimeout: TimeSpan.FromMinutes(10),
    ackTimeout: TimeSpan.FromSeconds(60), // drop a response whose client stops acking (default 60 s)
    writeTimeout: TimeSpan.FromSeconds(30)); // deadline for each frame the server sends
```

### Authentication

The binary endpoint serves the same schema as a GraphQL endpoint, so it carries that
endpoint's authentication requirement. It resolves the requirement from the endpoint it
serves — the one named by `graphqlPath`, or the single registered endpoint — and closes an
anonymous connection with a `PolicyViolation` close status before reading a frame. Nothing
is executed on such a connection.

The requirement is one setting, not two, so the transport and the endpoint cannot drift
apart. Serving anonymous callers is always an explicit choice: set `DisableAuth` on the
endpoint, or pass `requireAuthentication: false` on the mount. A mount that cannot identify
the endpoint it serves requires authentication.

Set `graphqlPath` whenever more than one GraphQL endpoint is registered. Without it the
mount cannot tell which endpoint it serves, so it resolves the schema by the
single-endpoint rule and requires authentication.

### Connection limits

Each mount has its own admission counter. The slot is taken when the socket is upgraded —
before the authentication check, because an accepted socket already costs a receive buffer
— and an upgrade over `maxConnections` is refused with HTTP 503 without being accepted.

Every read has a deadline. The first frame gets `firstFrameTimeout`, so a peer that
connects and then says nothing cannot hold an admission slot; later frames get
`idleTimeout`, which is generous because an established session is a pooled client that may
sit quiet between queries. A connection past its read deadline is sent a `PolicyViolation`
close frame; a peer that does not echo it within 5 seconds is dropped.

Every write has a deadline too. Each frame the server sends (results, chunks, upload acks,
error and close frames) must leave within `writeTimeout` (30 seconds by default). A peer that
stops reading, so its TCP window closes, cannot take a close frame either: the server aborts
the socket and frees its slot. A client that sends acks or NACKs a transfer cannot have asked
for (more than four ack windows unconsumed, or a NACK past the last chunk) is closed with
`ProtocolError`. The read, write, close-handshake and ack deadlines run on the host's
registered `TimeProvider`; the upload-reassembly and retransmit-buffer expiry still use the
system tick count.

### Concurrent requests

Requests on one connection run concurrently. One reader takes every frame off the socket and
hands each `ChunkAck` or `ChunkNack` to the transfer whose `request_id` it names; every frame
the server sends goes through one serialized writer. A slow query or a large chunked response
does not hold up the requests behind it: their frames interleave, and the client matches them
by `request_id`.

A connection runs at most 16 requests at once
(`BifrostBinaryMiddleware.MaxConcurrentRequestsPerConnection`). A request past that is refused
with an `Error` frame rather than queued, so resend it when one completes. `@bifrostql/binary-client`
does this for you: it sends at most 16 requests at once (`MAX_CONCURRENT_REQUESTS_PER_CONNECTION`),
holds the rest in a first-in, first-out queue, and resends a request the server refused this way.
A request the caller aborts keeps its slot until the server's response ends, because the server
keeps running it. A `request_id` must
be unique among the requests in flight; a request reusing one is refused the same way. Once the
client has acked every chunk of a response, that id is free again.

The transport's own refusals carry a machine-readable code in the `Error` frame's `error_code`
field (protobuf field 13, a string). Branch on the code, never on the frame's text:

| `error_code` | Meaning |
|---|---|
| `TOO_MANY_CONCURRENT_REQUESTS` | The connection already runs 16 requests. The request never ran; resend it when one completes. |
| `REQUEST_ID_IN_FLIGHT` | The `request_id` names a request still in flight. The request never ran; resend it under a fresh id. |
| `CHUNK_ACK_TIMEOUT` | The client did not ack the next chunk of this response within the mount's `ackTimeout` (60 s by default). Only this response was abandoned; the request ran, so resending re-runs it. |

The codes are public as `BinaryErrorCode` in both `BifrostQL.Server` and `@bifrostql/binary-client`.
GraphQL execution errors carry no frame code; they arrive in `errors` as on the HTTP endpoint.

Each request runs in its own DI scope, so scoped services are not shared between the requests
of one connection. The scope covers the session check, the identity projection and the
execution; it is disposed once the response is serialized, before the first frame is sent, so a
chunked response waiting on acks holds no scoped service (a `DbContext`, its connection).

Each request also gets its own `HttpContext`, and that is the one your
`IBifrostAuthContextFactory`, grant resolver, hooks and `IHttpContextAccessor` see. It is a copy
of the upgrade request, taken once when the connection opens: method, path, query string,
headers, connection addresses, client certificate, TLS handshake (`ITlsHandshakeFeature`),
trace identifier, endpoint (`GetEndpoint()`), route values (each request gets its own copy; `UseBifrostBinary` mounts on a path prefix, not
a route template, so they hold only what middleware before the mount set), and a shallow copy of
`Items`. Its `RequestServices` is the request's scope, and its `User` is the connection's
principal, refreshed when the local session check reissues it. `Items` is a new dictionary per
request, so a key one request adds or replaces is not seen by the next, but the values are the
upgrade request's objects, shared by reference across requests: a mutable value stored there at
upgrade time is shared state, and must be thread-safe or left unmodified. The copy has no
response to write to and no session: `ISession` is per request and not thread-safe, so
`HttpContext.Session` throws inside a request. Features your host added to the upgrade request
itself are not copied. Requests never read the upgrade request's own context, because
`HttpContext` is not thread-safe and they run concurrently.

Requests do not run under the upgrade request's execution context, so `AsyncLocal` state set
before the upgrade does not reach them: OpenTelemetry `Baggage.Current`, a host's own tenant or
impersonation `AsyncLocal`, and logging scopes pushed on the upgrade request are all absent. That
context carries the upgrade request in `IHttpContextAccessor`, and ASP.NET Core's accessor cannot
be pointed at a request's copy without clearing it for the upgrade request too. The trace parent
(`Activity.Current`, with its Activity baggage) and the cultures are carried over. Put
per-connection state a request needs in `Items` at upgrade time, or derive it from the request's
`HttpContext` (`User`, route values, headers).

Chunked uploads grow from the bytes that actually arrive. The `total_bytes` a client
declares bounds the transfer and feeds the admission caps (64 MB per transfer, 128 MB
across one connection), but the server never allocates from it: a transfer that declares
64 MB and delivers 40 bytes holds 40 bytes and is rejected when its last chunk arrives
short. A reassembled request keeps counting against the 128 MB connection cap until it
finishes running, so two 64 MB requests in flight use up the cap and the next upload chunk
is refused until one of them ends. A first chunk the server refuses opens no transfer.

`@bifrostql/binary-client` uses this for large requests. A request frame over the
server's 4 MiB frame limit is split into 1 MiB `Chunk` frames, sent back to back, and
the server runs the reassembled request as if it had arrived whole (it takes a
concurrency slot only once the last chunk is in). The server acks each upload chunk; the
client needs no ack to proceed and ignores them. A request over 64 MiB, the reassembly
limit, is rejected by the client before anything is sent.

The JSON `/graphql` endpoint and the binary `/bifrost-ws` endpoint can run side-by-side on the same server. Clients pick whichever transport fits the request.

:::note
The BifrostQL.UI desktop header can probe the binary endpoint, but the embedded `@standardbeagle/edit-db` editor still sends its table queries over the HTTP `uri` prop. Treat the desktop toggle as a transport health probe until the editor accepts a pluggable transport.
:::

## Client install

The TypeScript client is published as `@bifrostql/binary-client`:

```bash
pnpm add @bifrostql/binary-client
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
  `mutation ($input: Insert_users!) {
    users(insert: $input)
  }`,
  { input: { name: "Alice", email: "alice@example.com" } }
);

if (result.errors.length > 0) {
  console.error("Mutation errors:", result.errors);
} else {
  console.log("Inserted:", result.data);
}
```

`result.errors` holds the same messages the HTTP GraphQL endpoint returns, as plain strings: the
envelope has no slot for `extensions.code`. A write refused for its [operation id](/BifrostQL/guides/mutations/#operation-ids)
is the exception, because the client must act on it rather than retry: its text leads with the
code, as on the gRPC and MCP wires, for example
`OPERATION_ID_REQUIRED: this write requires an operation id.` The other `OPERATION_ID_*` codes
read the same way.

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
| `bytes` | This chunk's share of the response payload, the GraphQL response JSON (`{"data": ...}`). |
| `isLast` | True only when `sequence === totalChunks - 1`. |

Chunks arrive in sequence even if the wire delivered them out of order — the client buffers and reorders internally. Concatenating `bytes` across all chunks gives the same payload whether or not the server chunked the response: a chunk frame carries a slice of the whole serialized protobuf message, and the client strips that message's envelope, so the first chunk is a few bytes shorter than the rest. If the envelope carries errors, the iterator throws with their text before yielding any payload, as it does for an unchunked `Result` with errors. Mutations have a streaming counterpart, `client.streamMutation()`, with the same shape.

The client acks a chunk only when your loop takes it, so the server's ack window paces the transfer to your consumer and the client buffers about one window (8 chunks, 512 KB by default). A full window is backpressure with a deadline: the server waits up to the mount's `ackTimeout` (60 s by default) for the next ack. Three limits apply:

- A consumer that holds one chunk longer than `ackTimeout` loses that response. The server abandons it and sends an `Error` frame coded `CHUNK_ACK_TIMEOUT`; the connection and its other requests carry on. The query already ran, so resending re-runs it.
- The connection's `idleTimeout` (10 min by default) counts every frame the client sends, acks included. A connection that sends nothing that long is closed by it.
- End an iterator you stop reading with `break` (or `iterator.return()`), never by leaving it suspended. Ending it acks every chunk it still holds, and the client ack-drops the rest of the response. A suspended iterator holds its share of the server's buffered response until the connection ends.

A mount sets `ackTimeout` to change how long one response waits on a full window. Pass `ChunkSender.NoAckTimeout` to opt out and wait as long as the consumer takes; only `idleTimeout` then bounds a stalled response, and its buffered chunks stay pinned until the connection ends.

The client's own idle timeout (`requestTimeoutMs`) restarts on every chunk received or acked, and does not fire while the consumer still holds chunks it has not taken: that silence is the consumer's, not the server's.

## Auto-reconnect

When the WebSocket drops, the client opens a fresh socket using an exponential backoff (100 ms → 30 s with 25% jitter).

The server keeps sent chunks per connection, so another connection can never replay them, and a fresh socket has none of the old one's. Nothing resumes across a reconnect, so the client never sends a `Resume` frame. On the new socket it:

- re-sends each in-flight query from the start, dropping any partial chunked response;
- rejects each in-flight mutation, because it may already have committed and re-running it could apply it twice;
- fails each stream whose consumer has already received chunks, because a re-run would deliver them again. A stream that had received nothing is re-sent like a query (or rejected like a mutation).

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
pnpm dlx @bifrostql/codegen --proto-file ./schema/bifrost.proto --out ./generated
```

CLI flags:

| Flag | Description |
|---|---|
| `--proto-file <path>` | Path to a local `.proto` file. |
| `--out <dir>` | Output directory for generated `.ts` files (default `./generated`). |
| `--header <key=value>` | HTTP header to send on the WebSocket handshake (repeatable). |
| `-h`, `--help` | Show help and exit. |

The interfaces describe rows as they arrive in JSON, over GraphQL or in a binary `Result` payload. A 64-bit integer (`int64`, a `bigint` column) is a `string`, because the server's `BigInt` scalar writes every value as a decimal string, so a value past 2^53 keeps every digit. A `decimal` column is a proto `string` for the same reason: the `Decimal` scalar writes its exact decimal text, never a double. A `bytes` field (a `binary`, `varbinary` or `image` column) is a base64 `string`, because that is how the server writes blob values into JSON; decode it yourself when you need the bytes. Members of a `oneof`, such as the per-table `<table>_row` fields of `BifrostMessage`, are optional properties, since at most one is set.

A `--endpoint <ws-url>` flag is pre-wired for fetching the `.proto` text directly from a running server, but the server does not yet expose its proto schema as a GraphQL field. Until that lands, prefer `--proto-file` against a checked-in copy of the schema (the .NET `ProtoSchemaGeneratorTests` test can capture one for you).

## Troubleshooting

### Can I mix binary and JSON transports?

Yes. `UseBifrostBinary()` and `UseBifrostQL()` register independent middleware. The same server can serve both, and your clients are free to pick the right transport per request. They share the same schema and the same module pipeline.

### What happens if the server is down when I call `connect()`?

`connect()` rejects with the connect error on the very first attempt. After a successful connect, if the server later drops the socket, the client enters the auto-reconnect cycle and pending requests wait until the socket is back, where they are handled as [Auto-reconnect](#auto-reconnect) describes (or until `maxReconnectAttempts` is hit, at which point they reject with the last error).

### How do I force a fresh connection?

Call `client.close()` to drop the current socket, error any active streams with a "client closed" error, and stop the reconnect controller. Then construct a new `BifrostBinaryClient` and call `connect()` on it. The previous instance is permanently closed and cannot be reopened.

### How big are chunks?

The server defaults to a 64 KB chunk threshold and an 8-chunk ack window — payloads above 64 KB are chunked, and the server holds back at 8 unacked chunks to apply backpressure. Both values are tunable through the parameters of `UseBifrostBinary()`. `query()` acks each chunk as it arrives and reassembles the response in memory; `stream()` acks each chunk when your loop takes it (see [Streaming](#streaming)).
