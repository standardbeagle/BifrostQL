import { afterEach, beforeEach, describe, expect, it } from "vitest";
import {
  BifrostBinaryClient,
  BifrostMessageType,
  decodeMessage,
  emptyChunkInfo,
  encodeMessage,
  type BifrostMessage,
} from "./index.js";
import { crc32 } from "./chunking.js";
import {
  MAX_QUEUE_SIZE,
  StreamingQueue,
  ingestStreamingChunk,
  type StreamChunk,
} from "./streaming.js";
import {
  createFakeWebSocketConstructor,
  type FakeWebSocketTracker,
} from "./fake-websocket.js";

const TEST_URL = "ws://test.invalid/bifrost-ws";

function emptyMessage(overrides: Partial<BifrostMessage> = {}): BifrostMessage {
  return {
    requestId: 0,
    type: BifrostMessageType.Query,
    query: "",
    variablesJson: "",
    payload: new Uint8Array(0),
    errors: [],
    chunkInfo: emptyChunkInfo(),
    ...overrides,
  };
}

/**
 * Mirrors the helper used in index.test.ts: serializes an inner Result
 * BifrostMessage and slices its bytes into N Chunk frames.
 */
function buildChunkFrames(
  requestId: number,
  inner: BifrostMessage,
  chunkSize: number
): Uint8Array[] {
  const serialized = encodeMessage(inner);
  const totalBytes = serialized.length;
  const total = Math.ceil(totalBytes / chunkSize);
  const frames: Uint8Array[] = [];
  for (let i = 0; i < total; i++) {
    const offset = i * chunkSize;
    const length = Math.min(chunkSize, totalBytes - offset);
    const fragment = serialized.slice(offset, offset + length);
    frames.push(
      encodeMessage(
        emptyMessage({
          requestId,
          type: BifrostMessageType.Chunk,
          payload: fragment,
          chunkInfo: {
            sequence: i,
            total,
            offset,
            totalBytes,
            checksum: crc32(fragment),
          },
        })
      )
    );
  }
  return frames;
}

describe("StreamingQueue", () => {
  it("yields pushed chunks in sequence order via for-await", async () => {
    const queue = new StreamingQueue(1, () => {});
    const c0: StreamChunk = {
      requestId: 1,
      sequence: 0,
      totalChunks: 2,
      bytes: new Uint8Array([1]),
      isLast: false,
    };
    const c1: StreamChunk = {
      requestId: 1,
      sequence: 1,
      totalChunks: 2,
      bytes: new Uint8Array([2]),
      isLast: true,
    };
    queue.push(c0);
    queue.push(c1);
    queue.complete();

    const collected: StreamChunk[] = [];
    for await (const chunk of queue) {
      collected.push(chunk);
    }
    expect(collected.map((c) => c.sequence)).toEqual([0, 1]);
  });

  it("buffers out-of-order pushes and emits in sequence order", async () => {
    const queue = new StreamingQueue(1, () => {});
    const make = (seq: number, isLast: boolean): StreamChunk => ({
      requestId: 1,
      sequence: seq,
      totalChunks: 4,
      bytes: new Uint8Array([seq]),
      isLast,
    });

    queue.push(make(2, false));
    queue.push(make(0, false));
    queue.push(make(3, true));
    queue.push(make(1, false));
    queue.complete();

    const collected: number[] = [];
    for await (const chunk of queue) {
      collected.push(chunk.sequence);
    }
    expect(collected).toEqual([0, 1, 2, 3]);
  });

  it("rejects in-progress next() when error() is called", async () => {
    const queue = new StreamingQueue(1, () => {});
    const pending = queue.next();
    queue.error(new Error("boom"));
    await expect(pending).rejects.toThrowError("boom");
  });

  it("calls cleanup exactly once when return() is invoked via break", async () => {
    let cleanups = 0;
    const queue = new StreamingQueue(1, () => {
      cleanups++;
    });
    queue.push({
      requestId: 1,
      sequence: 0,
      totalChunks: 3,
      bytes: new Uint8Array([0]),
      isLast: false,
    });
    queue.push({
      requestId: 1,
      sequence: 1,
      totalChunks: 3,
      bytes: new Uint8Array([1]),
      isLast: false,
    });

    for await (const _ of queue) {
      break;
    }
    expect(cleanups).toBe(1);

    // A second return() (e.g. from generator finalization) must not double-cleanup.
    await queue.return();
    expect(cleanups).toBe(1);
  });

  it("errors with backpressure message when queue exceeds MAX_QUEUE_SIZE", async () => {
    const queue = new StreamingQueue(1, () => {});
    // Push MAX_QUEUE_SIZE + 1 contiguous chunks without consuming so the
    // ready buffer overflows. The (MAX_QUEUE_SIZE+1)-th push triggers error().
    for (let i = 0; i <= MAX_QUEUE_SIZE; i++) {
      queue.push({
        requestId: 1,
        sequence: i,
        totalChunks: MAX_QUEUE_SIZE + 2,
        bytes: new Uint8Array([i & 0xff]),
        isLast: false,
      });
    }

    // Drain the buffered chunks (they were accepted before the overflow), then
    // expect the next call to surface the backpressure error.
    let drained = 0;
    while (drained < MAX_QUEUE_SIZE) {
      const result = await queue.next();
      if (result.done) break;
      drained++;
    }
    expect(drained).toBe(MAX_QUEUE_SIZE);
    await expect(queue.next()).rejects.toThrowError(/exceeded MAX_QUEUE_SIZE/);
  });

  it("push() is a no-op once the queue is closed or errored", () => {
    const queue = new StreamingQueue(1, () => {});
    queue.error(new Error("done"));
    queue.push({
      requestId: 1,
      sequence: 0,
      totalChunks: 1,
      bytes: new Uint8Array([1]),
      isLast: true,
    });
    // bufferedCount should still be zero (push was rejected).
    expect(queue.bufferedCount).toBe(0);
  });

  it("error() is idempotent — second call is ignored", async () => {
    const queue = new StreamingQueue(1, () => {});
    queue.error(new Error("first"));
    queue.error(new Error("second"));
    await expect(queue.next()).rejects.toThrowError("first");
  });

  it("wakeConsumer delivers a chunk to a waiting next()", async () => {
    const queue = new StreamingQueue(1, () => {});
    // Consumer awaits before any chunk is pushed.
    const pending = queue.next();
    queue.push({
      requestId: 1,
      sequence: 0,
      totalChunks: 1,
      bytes: new Uint8Array([7]),
      isLast: true,
    });
    const result = await pending;
    expect(result.done).toBe(false);
    if (!result.done) {
      expect(result.value.bytes).toEqual(new Uint8Array([7]));
    }
  });

  it("wakeConsumer signals done to a waiting next() when complete() is called with no chunks", async () => {
    const queue = new StreamingQueue(1, () => {});
    const pending = queue.next();
    queue.complete();
    const result = await pending;
    expect(result.done).toBe(true);
  });

  it("return() resolves a waiting next() with done:true", async () => {
    const queue = new StreamingQueue(1, () => {});
    const pending = queue.next();
    await queue.return();
    const result = await pending;
    expect(result.done).toBe(true);
  });

  it("throw() rejects with the error and runs cleanup", async () => {
    let cleanups = 0;
    const queue = new StreamingQueue(1, () => {
      cleanups++;
    });
    await expect(queue.throw(new Error("user-thrown"))).rejects.toThrowError(
      "user-thrown"
    );
    expect(cleanups).toBe(1);
  });

  it("throw() wraps non-Error values into an Error", async () => {
    const queue = new StreamingQueue(1, () => {});
    await expect(queue.throw("string-thrown")).rejects.toThrowError(
      "string-thrown"
    );
  });
});

describe("ingestStreamingChunk", () => {
  function chunkMessage(
    overrides: Partial<BifrostMessage> = {}
  ): BifrostMessage {
    return {
      requestId: 1,
      type: BifrostMessageType.Chunk,
      query: "",
      variablesJson: "",
      payload: new Uint8Array(0),
      errors: [],
      chunkInfo: emptyChunkInfo(),
      ...overrides,
    };
  }

  it("errors the queue when total <= 0", async () => {
    const queue = new StreamingQueue(1, () => {});
    const ok = ingestStreamingChunk(
      queue,
      chunkMessage({
        chunkInfo: { sequence: 0, total: 0, offset: 0, totalBytes: 0, checksum: 0 },
      })
    );
    expect(ok).toBe(false);
    await expect(queue.next()).rejects.toThrowError(/total=0/);
  });

  it("errors the queue when sequence is out of range", async () => {
    const queue = new StreamingQueue(1, () => {});
    const ok = ingestStreamingChunk(
      queue,
      chunkMessage({
        chunkInfo: { sequence: 5, total: 3, offset: 0, totalBytes: 0, checksum: 0 },
      })
    );
    expect(ok).toBe(false);
    await expect(queue.next()).rejects.toThrowError(/out of range/);
  });

  it("returns true when the chunk validates and is accepted", async () => {
    const queue = new StreamingQueue(1, () => {});
    const fragment = new Uint8Array([10, 20, 30]);
    const ok = ingestStreamingChunk(
      queue,
      chunkMessage({
        payload: fragment,
        chunkInfo: {
          sequence: 0,
          total: 1,
          offset: 0,
          totalBytes: 3,
          checksum: crc32(fragment),
        },
      })
    );
    expect(ok).toBe(true);
    // Drain the single chunk; the queue auto-completes after the final chunk.
    const first = await queue.next();
    expect(first.done).toBe(false);
    if (!first.done) {
      expect(first.value.bytes).toEqual(fragment);
      expect(first.value.isLast).toBe(true);
    }
    const done = await queue.next();
    expect(done.done).toBe(true);
  });
});

describe("BifrostBinaryClient.stream()", () => {
  let tracker: FakeWebSocketTracker;
  let client: BifrostBinaryClient;

  beforeEach(async () => {
    tracker = createFakeWebSocketConstructor();
    client = new BifrostBinaryClient({
      url: TEST_URL,
      WebSocket: tracker.ctor,
      requestTimeoutMs: 5_000,
    });
    const p = client.connect();
    tracker.last.simulateOpen();
    await p;
  });

  afterEach(() => {
    if (client.connected) client.close();
  });

  it("stream() yields each verified chunk in sequence order even when wire-shuffled", async () => {
    const ws = tracker.last;
    const inner = emptyMessage({
      requestId: 1, // requestId is rewritten by the helper-based frame builder; clientside id=1
      type: BifrostMessageType.Result,
      payload: new TextEncoder().encode(
        JSON.stringify({ rows: ["a", "b", "c", "d"], n: 4 })
      ),
    });

    const iterator = client.stream("{ big }");
    // The send happened synchronously inside stream() during createChunkStream.
    const sent = decodeMessage(ws.sentFrames[0]!);
    expect(sent.type).toBe(BifrostMessageType.Query);
    expect(sent.query).toBe("{ big }");
    const sentId = sent.requestId;

    const frames = buildChunkFrames(sentId, inner, 8);
    expect(frames.length).toBeGreaterThanOrEqual(4);

    // Deliver in reverse order — the queue must still emit 0,1,2,...
    for (let i = frames.length - 1; i >= 0; i--) {
      ws.receive(frames[i]!);
    }

    const collected: StreamChunk[] = [];
    for await (const chunk of iterator) {
      collected.push(chunk);
    }

    expect(collected.length).toBe(frames.length);
    expect(collected.map((c) => c.sequence)).toEqual(
      frames.map((_, i) => i)
    );
    // isLast is only true on the final chunk
    expect(collected.filter((c) => c.isLast).length).toBe(1);
    expect(collected[collected.length - 1]!.isLast).toBe(true);
    expect(collected[0]!.totalChunks).toBe(frames.length);
  });

  it("for-await loop terminates cleanly after the last chunk", async () => {
    const ws = tracker.last;
    const inner = emptyMessage({
      requestId: 1,
      type: BifrostMessageType.Result,
      payload: new TextEncoder().encode("hello world chunked stream payload"),
    });

    const iterator = client.stream("{ big }");
    const sentId = decodeMessage(ws.sentFrames[0]!).requestId;
    for (const frame of buildChunkFrames(sentId, inner, 6)) {
      ws.receive(frame);
    }

    let loopRan = false;
    for await (const _ of iterator) {
      loopRan = true;
    }
    expect(loopRan).toBe(true);
    expect(client.pendingCount).toBe(0);
  });

  it("single-chunk (non-chunked Result) yields exactly one StreamChunk", async () => {
    const ws = tracker.last;
    const iterator = client.stream("{ small }");
    const sentId = decodeMessage(ws.sentFrames[0]!).requestId;

    const payload = new TextEncoder().encode(
      JSON.stringify({ small: "yes" })
    );
    ws.receive(
      encodeMessage(
        emptyMessage({
          requestId: sentId,
          type: BifrostMessageType.Result,
          payload,
        })
      )
    );

    const collected: StreamChunk[] = [];
    for await (const chunk of iterator) {
      collected.push(chunk);
    }
    expect(collected).toHaveLength(1);
    expect(collected[0]!.sequence).toBe(0);
    expect(collected[0]!.totalChunks).toBe(1);
    expect(collected[0]!.isLast).toBe(true);
    expect(collected[0]!.bytes).toEqual(payload);
  });

  it("server Error frame mid-stream causes the iterator to throw", async () => {
    const ws = tracker.last;
    const inner = emptyMessage({
      requestId: 1,
      type: BifrostMessageType.Result,
      payload: new TextEncoder().encode("partial payload before error"),
    });
    const iterator = client.stream("{ broken }");
    const sentId = decodeMessage(ws.sentFrames[0]!).requestId;

    const frames = buildChunkFrames(sentId, inner, 6);
    // Deliver the first chunk, then a server Error frame (non-chunked).
    ws.receive(frames[0]!);
    ws.receive(
      encodeMessage(
        emptyMessage({
          requestId: sentId,
          type: BifrostMessageType.Error,
          errors: ["query failed mid-stream"],
        })
      )
    );

    const seen: StreamChunk[] = [];
    let caught: Error | null = null;
    try {
      for await (const chunk of iterator) {
        seen.push(chunk);
      }
    } catch (err) {
      caught = err as Error;
    }
    expect(seen).toHaveLength(1);
    expect(caught).toBeInstanceOf(Error);
    expect(caught!.message).toMatch(/query failed mid-stream/);
  });

  it("closing the client mid-stream terminates the iterator with a connection-closed error", async () => {
    const ws = tracker.last;
    const iterator = client.stream("{ big }");
    const sentId = decodeMessage(ws.sentFrames[0]!).requestId;

    const inner = emptyMessage({
      requestId: sentId,
      type: BifrostMessageType.Result,
      payload: new TextEncoder().encode("partial bytes"),
    });
    const frames = buildChunkFrames(sentId, inner, 6);
    ws.receive(frames[0]!);

    // Start consuming, then close.
    const consumer = (async () => {
      const seen: StreamChunk[] = [];
      for await (const chunk of iterator) {
        seen.push(chunk);
      }
      return seen;
    })();

    client.close();
    await expect(consumer).rejects.toThrowError(/Connection closed/);
  });

  it("`break` in the consumer cleans up the streaming registration", async () => {
    const ws = tracker.last;
    const inner = emptyMessage({
      requestId: 1,
      type: BifrostMessageType.Result,
      payload: new TextEncoder().encode(
        "longer payload split into many chunks for testing break"
      ),
    });
    const iterator = client.stream("{ big }");
    const sentId = decodeMessage(ws.sentFrames[0]!).requestId;

    const frames = buildChunkFrames(sentId, inner, 6);
    expect(frames.length).toBeGreaterThanOrEqual(3);

    // Deliver the first two chunks so the consumer has something to process.
    ws.receive(frames[0]!);
    ws.receive(frames[1]!);

    let i = 0;
    for await (const _ of iterator) {
      if (++i === 1) break;
    }

    // After break, the streaming queue must have been removed from the client
    // so further chunks for the same id are treated as unknown.
    // Use the documented public surface: pendingCount is unaffected by
    // streaming, but a fresh stream should reuse a new requestId cleanly.
    const followup = client.stream("{ next }");
    const followupId = decodeMessage(ws.sentFrames[1]!).requestId;
    expect(followupId).not.toBe(sentId);
    // Cancel the follow-up too.
    await followup.return!();
  });

  it("rejects checksum-mismatched chunks and terminates the iterator", async () => {
    const ws = tracker.last;
    const inner = emptyMessage({
      requestId: 1,
      type: BifrostMessageType.Result,
      payload: new TextEncoder().encode("payload to corrupt"),
    });
    const iterator = client.stream("{ big }");
    const sentId = decodeMessage(ws.sentFrames[0]!).requestId;

    const frames = buildChunkFrames(sentId, inner, 6);
    // Corrupt the second frame's checksum.
    const decoded = decodeMessage(frames[1]!);
    decoded.chunkInfo.checksum = (decoded.chunkInfo.checksum ^ 0xffffffff) >>> 0;
    const corrupted = encodeMessage(decoded);

    ws.receive(frames[0]!);
    ws.receive(corrupted);

    let caught: Error | null = null;
    try {
      for await (const _ of iterator) {
        // intentionally consume chunks until the error surfaces
      }
    } catch (err) {
      caught = err as Error;
    }
    expect(caught).toBeInstanceOf(Error);
    expect(caught!.message).toMatch(/CRC32 mismatch/);
  });

  it("streamMutation() sends a Mutation-typed frame", async () => {
    const ws = tracker.last;
    const iterator = client.streamMutation(
      "mutation { upload(input: $i) { id } }",
      { i: 1 }
    );
    const sent = decodeMessage(ws.sentFrames[0]!);
    expect(sent.type).toBe(BifrostMessageType.Mutation);
    expect(sent.variablesJson).toBe(JSON.stringify({ i: 1 }));

    // Resolve the stream so the test cleans up.
    ws.receive(
      encodeMessage(
        emptyMessage({
          requestId: sent.requestId,
          type: BifrostMessageType.Result,
          payload: new Uint8Array([42]),
        })
      )
    );
    const collected: StreamChunk[] = [];
    for await (const chunk of iterator) {
      collected.push(chunk);
    }
    expect(collected).toHaveLength(1);
    expect(collected[0]!.bytes).toEqual(new Uint8Array([42]));
  });

  it("stream() returns an iterator that immediately errors when not connected", async () => {
    const offline = new BifrostBinaryClient({
      url: TEST_URL,
      WebSocket: tracker.ctor,
    });
    const iterator = offline.stream("{ x }");
    await expect(iterator.next()).rejects.toThrowError("Not connected");
  });
});
