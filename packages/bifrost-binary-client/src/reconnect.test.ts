import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  BifrostBinaryClient,
  BifrostMessageType,
  RESUME_NO_CHUNKS_RECEIVED,
  decodeMessage,
  emptyChunkInfo,
  encodeMessage,
  type BifrostMessage,
} from "./index.js";
import { crc32 } from "./chunking.js";
import {
  ExponentialBackoff,
  ReconnectController,
  type BackoffPolicy,
} from "./reconnect.js";
import {
  FakeWebSocket,
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
    lastSequence: 0,
    ...overrides,
  };
}

function jsonPayload(value: unknown): Uint8Array {
  return new TextEncoder().encode(JSON.stringify(value));
}

/**
 * Slices a serialized inner Result message into N Chunk frames the way the
 * server's ChunkSender does. Mirrors the helper used in the existing
 * index.test.ts and streaming.test.ts so the tests exercise the same wire
 * shape the server emits.
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

describe("ExponentialBackoff", () => {
  it("produces the expected sequence with deterministic random=0.5 (no jitter offset)", () => {
    // random=0.5 → multiplier = 1 - jitter + 0.5 * 2 * jitter = 1.0 exactly.
    const backoff = new ExponentialBackoff({ random: () => 0.5 });
    const delays = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11].map((a) =>
      backoff.nextDelayMs(a)
    );
    expect(delays).toEqual([
      100, 200, 400, 800, 1600, 3200, 6400, 12800, 25600, 30000, 30000,
    ]);
  });

  it("applies the lower jitter bound when random=0", () => {
    const backoff = new ExponentialBackoff({ random: () => 0 });
    // base 100 → multiplier 1 - 0.25 = 0.75 → 75
    expect(backoff.nextDelayMs(1)).toBe(75);
    // base 200 → multiplier 0.75 → 150
    expect(backoff.nextDelayMs(2)).toBe(150);
  });

  it("applies the upper jitter bound when random≈1", () => {
    const backoff = new ExponentialBackoff({ random: () => 0.9999999 });
    // base 100 → multiplier = 0.75 + 0.9999999 * 0.5 ≈ 1.24999995 → floor → 124
    expect(backoff.nextDelayMs(1)).toBe(124);
    // Pinning the value with random=1 (theoretical upper) would round to 125.
    const upper = new ExponentialBackoff({ random: () => 1 });
    expect(upper.nextDelayMs(1)).toBe(125);
  });

  it("respects custom initialMs / maxMs / factor / jitter", () => {
    const backoff = new ExponentialBackoff({
      initialMs: 50,
      maxMs: 1000,
      factor: 3,
      jitter: 0,
      random: () => 0,
    });
    expect(backoff.nextDelayMs(1)).toBe(50);
    expect(backoff.nextDelayMs(2)).toBe(150);
    expect(backoff.nextDelayMs(3)).toBe(450);
    expect(backoff.nextDelayMs(4)).toBe(1000); // capped
    expect(backoff.nextDelayMs(99)).toBe(1000);
  });

  it("rejects invalid options", () => {
    expect(() => new ExponentialBackoff({ initialMs: 0 })).toThrowError();
    expect(() => new ExponentialBackoff({ initialMs: 100, maxMs: 50 })).toThrowError();
    expect(() => new ExponentialBackoff({ factor: 0.5 })).toThrowError();
    expect(() => new ExponentialBackoff({ jitter: -0.1 })).toThrowError();
    expect(() => new ExponentialBackoff({ jitter: 1.5 })).toThrowError();
  });

  it("rejects attempt < 1", () => {
    const backoff = new ExponentialBackoff();
    expect(() => backoff.nextDelayMs(0)).toThrowError();
    expect(() => backoff.nextDelayMs(-1)).toThrowError();
  });

  it("reset() is safe to call (stateless)", () => {
    const backoff = new ExponentialBackoff({ random: () => 0.5 });
    expect(backoff.nextDelayMs(1)).toBe(100);
    backoff.reset();
    expect(backoff.nextDelayMs(1)).toBe(100);
  });

  it("handles jitter=0 by returning the unmodified base", () => {
    const backoff = new ExponentialBackoff({ jitter: 0, random: () => 0.123 });
    expect(backoff.nextDelayMs(1)).toBe(100);
    expect(backoff.nextDelayMs(2)).toBe(200);
  });
});

describe("ReconnectController", () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  /** Linear backoff — easier to assert against fake timers than exponential. */
  class FixedBackoff implements BackoffPolicy {
    public resetCount = 0;
    constructor(public readonly delayMs: number) {}
    nextDelayMs(): number {
      return this.delayMs;
    }
    reset(): void {
      this.resetCount++;
    }
  }

  it("schedules a retry after the policy delay and invokes connect()", async () => {
    const connect = vi.fn().mockResolvedValue(undefined);
    const onAttempt = vi.fn();
    const onSuccess = vi.fn();
    const ctrl = new ReconnectController({
      policy: new FixedBackoff(1000),
      connect,
      onAttempt,
      onSuccess,
    });

    ctrl.start(new Error("boom"));
    expect(ctrl.currentState).toBe("waiting");
    expect(onAttempt).toHaveBeenCalledWith(1, 1000);
    expect(connect).not.toHaveBeenCalled();

    await vi.advanceTimersByTimeAsync(999);
    expect(connect).not.toHaveBeenCalled();

    await vi.advanceTimersByTimeAsync(1);
    expect(connect).toHaveBeenCalledOnce();
    expect(onSuccess).toHaveBeenCalledWith(1);
    expect(ctrl.currentState).toBe("idle");
    expect(ctrl.attemptCount).toBe(0);
  });

  it("retries with increasing attempt numbers when connect() rejects", async () => {
    const connect = vi
      .fn<() => Promise<void>>()
      .mockRejectedValueOnce(new Error("fail-1"))
      .mockRejectedValueOnce(new Error("fail-2"))
      .mockResolvedValueOnce(undefined);
    const policy = new FixedBackoff(500);
    const onAttempt = vi.fn();
    const onSuccess = vi.fn();
    const ctrl = new ReconnectController({ policy, connect, onAttempt, onSuccess });

    ctrl.start(new Error("initial"));
    expect(onAttempt).toHaveBeenLastCalledWith(1, 500);

    await vi.advanceTimersByTimeAsync(500);
    // First attempt rejected → controller should schedule attempt 2.
    expect(connect).toHaveBeenCalledTimes(1);
    // Wait for the rejection's microtask + the next setTimeout schedule.
    await vi.advanceTimersByTimeAsync(0);
    expect(onAttempt).toHaveBeenLastCalledWith(2, 500);

    await vi.advanceTimersByTimeAsync(500);
    expect(connect).toHaveBeenCalledTimes(2);
    await vi.advanceTimersByTimeAsync(0);

    await vi.advanceTimersByTimeAsync(500);
    expect(connect).toHaveBeenCalledTimes(3);
    await vi.advanceTimersByTimeAsync(0);

    expect(onSuccess).toHaveBeenCalledWith(3);
    expect(ctrl.currentState).toBe("idle");
    expect(policy.resetCount).toBe(1);
  });

  it("cancel() stops a pending retry", async () => {
    const connect = vi.fn().mockResolvedValue(undefined);
    const ctrl = new ReconnectController({
      policy: new FixedBackoff(2000),
      connect,
    });

    ctrl.start(new Error("boom"));
    expect(ctrl.currentState).toBe("waiting");

    ctrl.cancel();
    expect(ctrl.currentState).toBe("idle");

    await vi.advanceTimersByTimeAsync(5000);
    expect(connect).not.toHaveBeenCalled();
  });

  it("invokes onGiveUp when maxAttempts is exhausted", async () => {
    const connect = vi
      .fn<() => Promise<void>>()
      .mockRejectedValue(new Error("nope"));
    const onGiveUp = vi.fn();
    const ctrl = new ReconnectController({
      policy: new FixedBackoff(100),
      connect,
      maxAttempts: 2,
      onGiveUp,
    });

    ctrl.start(new Error("trigger"));
    await vi.advanceTimersByTimeAsync(100);
    await vi.advanceTimersByTimeAsync(0);
    await vi.advanceTimersByTimeAsync(100);
    await vi.advanceTimersByTimeAsync(0);
    // Third attempt blocked by maxAttempts → onGiveUp fires immediately on the
    // post-rejection scheduleNext call.
    expect(connect).toHaveBeenCalledTimes(2);
    expect(onGiveUp).toHaveBeenCalledOnce();
    expect(onGiveUp.mock.calls[0]![0]).toBe(2);
    expect((onGiveUp.mock.calls[0]![1] as Error).message).toBe("nope");
    expect(ctrl.currentState).toBe("stopped");
  });

  it("stop() prevents any further reconnect work even after success races", async () => {
    let resolveConnect: () => void = () => {};
    const connect = vi
      .fn<() => Promise<void>>()
      .mockImplementation(() => new Promise<void>((res) => {
        resolveConnect = res;
      }));
    const onSuccess = vi.fn();
    const ctrl = new ReconnectController({
      policy: new FixedBackoff(100),
      connect,
      onSuccess,
    });

    ctrl.start(new Error("boom"));
    await vi.advanceTimersByTimeAsync(100);
    expect(connect).toHaveBeenCalledOnce();
    expect(ctrl.currentState).toBe("connecting");

    ctrl.stop();
    expect(ctrl.currentState).toBe("stopped");

    // Now resolve the in-flight connect - it should be ignored.
    resolveConnect();
    await vi.advanceTimersByTimeAsync(0);
    expect(onSuccess).not.toHaveBeenCalled();
    expect(ctrl.currentState).toBe("stopped");
  });

  it("start() is idempotent while already waiting", () => {
    const connect = vi.fn().mockResolvedValue(undefined);
    const onAttempt = vi.fn();
    const ctrl = new ReconnectController({
      policy: new FixedBackoff(1000),
      connect,
      onAttempt,
    });

    ctrl.start(new Error("a"));
    ctrl.start(new Error("b"));
    ctrl.start(new Error("c"));
    expect(onAttempt).toHaveBeenCalledTimes(1);
  });
});

describe("BifrostBinaryClient auto-reconnect", () => {
  let tracker: FakeWebSocketTracker;

  beforeEach(() => {
    vi.useFakeTimers();
    tracker = createFakeWebSocketConstructor();
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  /**
   * Helper: build a client with a deterministic backoff (no jitter, fixed
   * 100ms) and connect it through the FakeWebSocket so tests can drive
   * disconnects and inspect outbound frames.
   */
  async function makeConnectedClient(opts: {
    maxReconnectAttempts?: number;
    onReconnect?: (attempt: number) => void;
    onReconnectFailed?: (attempts: number, error: Error) => void;
  } = {}): Promise<BifrostBinaryClient> {
    const client = new BifrostBinaryClient({
      url: TEST_URL,
      WebSocket: tracker.ctor,
      requestTimeoutMs: 60_000,
      backoff: new ExponentialBackoff({
        initialMs: 100,
        factor: 1, // constant 100ms for predictable timing
        jitter: 0,
        random: () => 0.5,
      }),
      ...opts,
    });
    const p = client.connect();
    tracker.last.simulateOpen();
    await p;
    return client;
  }

  it("reconnects after an abnormal close and replays a pending non-chunked request", async () => {
    const onReconnect = vi.fn();
    const client = await makeConnectedClient({ onReconnect });
    const firstWs = tracker.last;

    // Issue a query, then drop the connection before the response arrives.
    const promise = client.query("{ users { id } }", { limit: 5 });
    expect(firstWs.sentFrames).toHaveLength(1);
    const original = decodeMessage(firstWs.sentFrames[0]!);
    expect(original.type).toBe(BifrostMessageType.Query);

    firstWs.simulateClose(1006, "abnormal");
    expect(client.connected).toBe(false);

    // Wait for the reconnect delay, then advance microtasks so openSocket runs.
    await vi.advanceTimersByTimeAsync(100);
    expect(tracker.instances).toHaveLength(2);
    const secondWs = tracker.last;
    expect(secondWs).not.toBe(firstWs);

    secondWs.simulateOpen();
    await vi.advanceTimersByTimeAsync(0);

    expect(onReconnect).toHaveBeenCalledWith(1);
    expect(client.connected).toBe(true);

    // The pending request had no partial chunks → must be re-sent as a fresh
    // Query frame on the new socket (NOT a Resume frame).
    expect(secondWs.sentFrames).toHaveLength(1);
    const replayed = decodeMessage(secondWs.sentFrames[0]!);
    expect(replayed.type).toBe(BifrostMessageType.Query);
    expect(replayed.requestId).toBe(original.requestId);
    expect(replayed.query).toBe("{ users { id } }");
    expect(replayed.variablesJson).toBe(JSON.stringify({ limit: 5 }));

    // Now respond on the new socket and the original promise should resolve.
    secondWs.receive(
      encodeMessage(
        emptyMessage({
          requestId: replayed.requestId,
          type: BifrostMessageType.Result,
          payload: jsonPayload({ users: [{ id: "u1" }] }),
        })
      )
    );
    const result = await promise;
    expect(result.data).toEqual({ users: [{ id: "u1" }] });
    expect(client.pendingCount).toBe(0);

    client.close();
  });

  it("sends a Resume frame for a pending request that had received some chunks", async () => {
    const client = await makeConnectedClient();
    const firstWs = tracker.last;

    const promise = client.query("{ big }");
    // Capture resolution/rejection eagerly so the assertion isn't sensitive to
    // fake-timer microtask ordering races between the disconnect cleanup and
    // the post-reconnect delivery.
    let resolved: { data: unknown; errors: string[] } | undefined;
    let rejected: Error | undefined;
    void promise.then(
      (r) => {
        resolved = r;
      },
      (e: Error) => {
        rejected = e;
      }
    );
    const requestId = decodeMessage(firstWs.sentFrames[0]!).requestId;

    // Build a multi-chunk inner Result. The exact frame count depends on the
    // serialized envelope size, so we deliver the first half before the
    // disconnect and let the server retransmit the rest after reconnect.
    const inner = emptyMessage({
      requestId,
      type: BifrostMessageType.Result,
      payload: jsonPayload({ rows: ["a", "b", "c", "d"], n: 4 }),
    });
    const frames = buildChunkFrames(requestId, inner, 8);
    const total = frames.length;
    expect(total).toBeGreaterThanOrEqual(4);
    const halfway = Math.floor(total / 2); // first `halfway` chunks delivered

    for (let i = 0; i < halfway; i++) {
      firstWs.receive(frames[i]!);
    }
    expect(client.pendingCount).toBe(1);

    // Drop the connection.
    firstWs.simulateClose(1006, "transient");
    await vi.advanceTimersByTimeAsync(100);
    expect(tracker.instances).toHaveLength(2);
    const secondWs = tracker.last;
    secondWs.simulateOpen();
    await vi.advanceTimersByTimeAsync(0);

    // Should send a Resume frame with last_sequence = (halfway - 1) (highest
    // contiguous chunk that arrived before the disconnect).
    expect(secondWs.sentFrames).toHaveLength(1);
    const resumeFrame = decodeMessage(secondWs.sentFrames[0]!);
    expect(resumeFrame.type).toBe(BifrostMessageType.Resume);
    expect(resumeFrame.requestId).toBe(requestId);
    expect(resumeFrame.lastSequence).toBe(halfway - 1);

    // Server retransmits the remaining chunks; the existing per-request
    // reassembler merges them with the chunks already buffered before the
    // disconnect to produce the full message.
    for (let i = halfway; i < total; i++) {
      secondWs.receive(frames[i]!);
    }
    await vi.advanceTimersByTimeAsync(0);

    expect(rejected).toBeUndefined();
    expect(resolved).toBeDefined();
    expect(resolved!.data).toEqual({ rows: ["a", "b", "c", "d"], n: 4 });
    expect(client.pendingCount).toBe(0);

    client.close();
  });

  it("uses the no-chunks sentinel when the first reconnect happens before any chunk arrives", async () => {
    // A pending chunked request that never received chunk 0 should fall back
    // to re-sending the original frame (because resumeFromByRequestId stays
    // empty for it). This covers the "no partial chunks" branch end-to-end.
    const client = await makeConnectedClient();
    const firstWs = tracker.last;

    const promise = client.query("{ big }");
    const requestId = decodeMessage(firstWs.sentFrames[0]!).requestId;

    firstWs.simulateClose(1006, "instant");
    await vi.advanceTimersByTimeAsync(100);
    const secondWs = tracker.last;
    secondWs.simulateOpen();
    await vi.advanceTimersByTimeAsync(0);

    // No partial chunks → original Query frame is re-sent, NOT a Resume frame.
    expect(secondWs.sentFrames).toHaveLength(1);
    const replay = decodeMessage(secondWs.sentFrames[0]!);
    expect(replay.type).toBe(BifrostMessageType.Query);
    expect(replay.requestId).toBe(requestId);
    expect(replay.lastSequence).toBe(0);

    // Sentinel constant exists for the inverse branch.
    expect(RESUME_NO_CHUNKS_RECEIVED).toBe(0xffffffff);

    secondWs.receive(
      encodeMessage(
        emptyMessage({
          requestId,
          type: BifrostMessageType.Result,
          payload: jsonPayload({ ok: true }),
        })
      )
    );
    await expect(promise).resolves.toMatchObject({ data: { ok: true } });

    client.close();
  });

  it("does NOT reconnect when client.close() initiates the disconnect", async () => {
    const client = await makeConnectedClient();
    const firstWs = tracker.last;

    const p1 = client.query("{ a }");
    const a1 = expect(p1).rejects.toThrowError(/Connection closed/);
    client.close();
    await a1;

    // Advance time well past any backoff delay; no second socket should be created.
    await vi.advanceTimersByTimeAsync(10_000);
    expect(tracker.instances).toHaveLength(1);
    expect(client.connected).toBe(false);
    expect(firstWs.closeCalls).toEqual([{ code: 1000, reason: "Client closed" }]);
  });

  it("does NOT reconnect on a normal close (code 1000) initiated by the server", async () => {
    const client = await makeConnectedClient();
    const firstWs = tracker.last;
    void client.query("{ a }").catch(() => {});

    firstWs.simulateClose(1000, "normal");
    await vi.advanceTimersByTimeAsync(10_000);
    expect(tracker.instances).toHaveLength(1);
    expect(client.connected).toBe(false);

    client.close();
  });

  it("rejects pending requests and fires onReconnectFailed when maxReconnectAttempts is exhausted", async () => {
    const onReconnectFailed = vi.fn();
    const client = await makeConnectedClient({
      maxReconnectAttempts: 2,
      onReconnectFailed,
    });
    const firstWs = tracker.last;
    const promise = client.query("{ a }");
    // Attach the rejection assertion immediately so the rejection is never
    // briefly unhandled (vitest treats fake-timer-microtask races as unhandled
    // rejections otherwise).
    const rejectionAssertion = expect(promise).rejects.toThrowError(
      /Reconnect failed after 2 attempts/
    );

    firstWs.simulateClose(1006, "boom");

    // Attempt 1: a new socket is created after the backoff delay; we close it
    // before it opens to simulate a failed connect.
    await vi.advanceTimersByTimeAsync(100);
    expect(tracker.instances).toHaveLength(2);
    tracker.instances[1]!.simulateClose(1006, "still down");
    await vi.advanceTimersByTimeAsync(0);

    // Attempt 2: same thing.
    await vi.advanceTimersByTimeAsync(100);
    expect(tracker.instances).toHaveLength(3);
    tracker.instances[2]!.simulateClose(1006, "still down");
    await vi.advanceTimersByTimeAsync(0);

    // After 2 failed attempts the controller gives up; the pending request is
    // rejected and onReconnectFailed fires.
    await rejectionAssertion;
    expect(onReconnectFailed).toHaveBeenCalledOnce();
    expect(onReconnectFailed.mock.calls[0]![0]).toBe(2);
    expect(client.pendingCount).toBe(0);

    client.close();
  });

  it("resumes a streaming request mid-flight: server replays chunks 2-3, iterator yields all 4", async () => {
    const client = await makeConnectedClient();
    const firstWs = tracker.last;

    const stream = client.stream("{ download }");
    const requestId = decodeMessage(firstWs.sentFrames[0]!).requestId;

    // Build 4 chunks; deliver 0 and 1, then drop the connection.
    const inner = emptyMessage({
      requestId,
      type: BifrostMessageType.Result,
      payload: jsonPayload({ rows: ["a", "b", "c", "d"] }),
    });
    const frames = buildChunkFrames(requestId, inner, 8);
    expect(frames.length).toBe(4);

    firstWs.receive(frames[0]!);
    firstWs.receive(frames[1]!);

    firstWs.simulateClose(1006, "drop");
    await vi.advanceTimersByTimeAsync(100);
    const secondWs = tracker.last;
    secondWs.simulateOpen();
    await vi.advanceTimersByTimeAsync(0);

    // Resume frame should ask for chunks after sequence 1.
    const resumeFrame = decodeMessage(secondWs.sentFrames[0]!);
    expect(resumeFrame.type).toBe(BifrostMessageType.Resume);
    expect(resumeFrame.lastSequence).toBe(1);

    // Server replays chunks 2 and 3 on the new socket.
    secondWs.receive(frames[2]!);
    secondWs.receive(frames[3]!);

    // Iterator yields all 4 chunks in sequence order.
    const collected: number[] = [];
    for await (const chunk of stream) {
      collected.push(chunk.sequence);
      if (chunk.isLast) break;
    }
    expect(collected).toEqual([0, 1, 2, 3]);

    client.close();
  });

  it("re-sends the original Query frame for a streaming request that received no chunks before disconnect", async () => {
    const client = await makeConnectedClient();
    const firstWs = tracker.last;

    const stream = client.stream("{ download }", { limit: 99 });
    const requestId = decodeMessage(firstWs.sentFrames[0]!).requestId;

    firstWs.simulateClose(1006, "drop");
    await vi.advanceTimersByTimeAsync(100);
    const secondWs = tracker.last;
    secondWs.simulateOpen();
    await vi.advanceTimersByTimeAsync(0);

    // Should be a fresh Query frame, not a Resume.
    const replay = decodeMessage(secondWs.sentFrames[0]!);
    expect(replay.type).toBe(BifrostMessageType.Query);
    expect(replay.requestId).toBe(requestId);
    expect(replay.query).toBe("{ download }");
    expect(replay.variablesJson).toBe(JSON.stringify({ limit: 99 }));

    // Now deliver the full single-chunk response so the iterator completes.
    secondWs.receive(
      encodeMessage(
        emptyMessage({
          requestId,
          type: BifrostMessageType.Result,
          payload: jsonPayload({ ok: true }),
        })
      )
    );

    const collected: number[] = [];
    for await (const chunk of stream) {
      collected.push(chunk.sequence);
      if (chunk.isLast) break;
    }
    expect(collected).toEqual([0]);

    client.close();
  });

  it("ignores ResumeAck frames at the top level", async () => {
    const client = await makeConnectedClient();
    const ws = tracker.last;
    void client.query("{ a }").catch(() => {});

    // Inject a ResumeAck frame for the pending request: client should silently
    // accept it without resolving the promise (ResumeAck just signals that
    // chunks are about to follow).
    ws.receive(
      encodeMessage(
        emptyMessage({
          requestId: 1,
          type: BifrostMessageType.ResumeAck,
        })
      )
    );
    expect(client.pendingCount).toBe(1);

    client.close();
  });

  it("clears all internal maps after close()", async () => {
    const client = await makeConnectedClient();
    const ws = tracker.last;

    const p1 = client.query("{ a }").catch(() => {});
    const p2 = client.query("{ b }").catch(() => {});
    const stream = client.stream("{ c }");
    void (async () => {
      try {
        for await (const _chunk of stream) {
          // drain
        }
      } catch {
        // ignore
      }
    })();

    expect(client.pendingCount).toBeGreaterThan(0);
    client.close();
    await Promise.all([p1, p2]);

    expect(client.pendingCount).toBe(0);
    expect(client.connected).toBe(false);
    expect(ws.closeCalls).toEqual([{ code: 1000, reason: "Client closed" }]);
  });

  it("disabling autoReconnect makes the client behave like the legacy close path", async () => {
    const client = new BifrostBinaryClient({
      url: TEST_URL,
      WebSocket: tracker.ctor,
      autoReconnect: false,
    });
    const cp = client.connect();
    tracker.last.simulateOpen();
    await cp;

    const p = client.query("{ a }");
    const assertion = expect(p).rejects.toThrowError(/Connection closed/);
    tracker.last.simulateClose(1006, "drop");
    await assertion;

    // No second socket created.
    await vi.advanceTimersByTimeAsync(10_000);
    expect(tracker.instances).toHaveLength(1);
  });

  it("uses the default ExponentialBackoff when no backoff option is provided", async () => {
    // Just make sure the default-constructed controller exists and survives a
    // disconnect without throwing. We don't assert exact timing because the
    // default jitter is non-deterministic; we only verify a second socket was
    // created within the maxMs window.
    const client = new BifrostBinaryClient({
      url: TEST_URL,
      WebSocket: tracker.ctor,
    });
    const p = client.connect();
    tracker.last.simulateOpen();
    await p;

    void client.query("{ a }").catch(() => {});
    tracker.last.simulateClose(1006, "drop");

    // Default initial is 100ms with 25% jitter; 200ms is comfortably past the
    // upper jitter bound of 125ms.
    await vi.advanceTimersByTimeAsync(200);
    expect(tracker.instances.length).toBeGreaterThanOrEqual(2);

    client.close();
  });
});

describe("FakeWebSocket sanity (used by reconnect tests)", () => {
  it("creates a fresh instance on each construction", () => {
    const tracker = createFakeWebSocketConstructor();
    new tracker.ctor("ws://a");
    new tracker.ctor("ws://b");
    expect(tracker.instances).toHaveLength(2);
    expect(tracker.last).toBeInstanceOf(FakeWebSocket);
  });
});
