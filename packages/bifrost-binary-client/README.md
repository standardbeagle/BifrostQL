# @bifrostql/binary-client

TypeScript client for the BifrostQL WebSocket binary transport. Speaks protobuf-encoded GraphQL over a single multiplexed WebSocket, supports async-iterator streaming for chunked responses, and reconnects automatically after a transient disconnect.

## Install

```bash
npm install @bifrostql/binary-client
```

## Query

```ts
import { BifrostBinaryClient } from "@bifrostql/binary-client";

const client = new BifrostBinaryClient({
  url: "ws://localhost:5000/bifrost-ws",
});

await client.connect();

const result = await client.query("{ users { id name email } }");
console.log(result.data);

client.close();
```

Run multiple queries concurrently over the same socket with `Promise.all`. The server runs at most 16 requests per connection, so the client sends 16 at a time and queues the rest (`client.queuedCount`). Use `client.mutate(text, variables)` for mutations.

A request whose frame is larger than the server's 4 MiB frame limit (`MAX_REQUEST_FRAME_BYTES`) goes out as 1 MiB chunks, which the server reassembles. A request over the server's 64 MiB reassembly limit (`MAX_REQUEST_BYTES`) is rejected before anything is sent.

## Stream

For large or binary responses (downloads, generated reports) iterate chunks as they arrive instead of waiting for the full transfer:

```ts
for await (const chunk of client.stream("{ download_large_blob }")) {
  console.log(`chunk ${chunk.sequence + 1}/${chunk.totalChunks}`);
  if (chunk.isLast) console.log("download complete");
}
```

Each chunk is CRC32-verified inline and yielded in `sequence` order. `chunk.bytes` holds only payload bytes (the GraphQL response JSON), so concatenating them gives the same bytes `query()` would parse, whether or not the server chunked the response.

A chunk is acked only when your loop takes it, so a slow consumer slows the server down instead of filling memory. Don't hold one chunk past the server's 30 s ack timeout (it closes the connection), and end an iterator you stop reading with `break` rather than leaving it suspended.

`client.streamMutation()` is the streaming counterpart for mutations.

## Auto-reconnect

When the socket drops, the client opens a fresh connection with exponential backoff. The server keeps a transfer's chunks only for the connection that carried it, so nothing resumes on the new socket. Instead the client:

- re-sends each in-flight query from the start;
- rejects each in-flight mutation, because it may already have committed and a re-send could apply it twice;
- fails each stream whose consumer has already received chunks, because a re-run would deliver them again.

Disable or tune via `BifrostClientOptions.autoReconnect`, `maxReconnectAttempts`, `backoff`, `onReconnect`, and `onReconnectFailed`.

## Server setup

Mount the WebSocket endpoint in your BifrostQL server with `app.UseBifrostBinary()` (default path `/bifrost-ws`). See the full guide for details.

## Documentation

Full guide: [BifrostQL Binary Transport Guide](https://dev.standardbeagle.com/BifrostQL/guides/binary-transport/) (also at `docs/src/content/docs/guides/binary-transport.md` in the repo).

## License

ISC
