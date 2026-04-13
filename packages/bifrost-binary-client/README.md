# @bifrostql/binary-client

TypeScript client for the BifrostQL WebSocket binary transport. Speaks protobuf-encoded GraphQL over a single multiplexed WebSocket, supports async-iterator streaming for chunked responses, and automatically resumes interrupted transfers across transient disconnects.

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

Run multiple queries concurrently over the same socket with `Promise.all`. Use `client.mutate(text, variables)` for mutations.

## Stream

For large or binary responses (downloads, generated reports) iterate chunks as they arrive instead of waiting for the full transfer:

```ts
for await (const chunk of client.stream("{ download_large_blob }")) {
  console.log(`chunk ${chunk.sequence + 1}/${chunk.totalChunks}`);
  if (chunk.isLast) console.log("download complete");
}
```

Each chunk is CRC32-verified inline and yielded in `sequence` order. `client.streamMutation()` is the streaming counterpart for mutations.

## Auto-resume

When the socket drops mid-transfer, the client snapshots the highest contiguous chunk per request, opens a fresh connection with exponential backoff, and sends a `Resume` frame so the server retransmits only the missing tail. Disable or tune via `BifrostClientOptions.autoReconnect`, `maxReconnectAttempts`, `backoff`, `onReconnect`, and `onReconnectFailed`.

## Server setup

Mount the WebSocket endpoint in your BifrostQL server with `app.UseBifrostBinary()` (default path `/bifrost-ws`). See the full guide for details.

## Documentation

Full guide: [BifrostQL Binary Transport Guide](https://standardbeagle.github.io/BifrostQL/guides/binary-transport/) (also at `docs/src/content/docs/guides/binary-transport.md` in the repo).

## License

ISC
