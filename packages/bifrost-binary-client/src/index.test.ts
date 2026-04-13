import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  BifrostBinaryClient,
  BifrostMessageType,
  decodeMessage,
  encodeMessage,
  type BifrostMessage,
} from "./index.js";
import {
  FakeWebSocket,
  FakeWebSocketReadyState,
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
    ...overrides,
  };
}

function jsonPayload(value: unknown): Uint8Array {
  return new TextEncoder().encode(JSON.stringify(value));
}

describe("encodeMessage / decodeMessage", () => {
  it("round-trips a fully-populated message", () => {
    const original = emptyMessage({
      requestId: 42,
      type: BifrostMessageType.Mutation,
      query: "mutation { insert_users(input: {name: \"Alice\"}) { id } }",
      variablesJson: JSON.stringify({ name: "Alice", age: 30 }),
      payload: new Uint8Array([1, 2, 3, 4, 5]),
      errors: ["first", "second", "third"],
    });

    const decoded = decodeMessage(encodeMessage(original));

    expect(decoded.requestId).toBe(42);
    expect(decoded.type).toBe(BifrostMessageType.Mutation);
    expect(decoded.query).toBe(original.query);
    expect(decoded.variablesJson).toBe(original.variablesJson);
    expect(Array.from(decoded.payload)).toEqual([1, 2, 3, 4, 5]);
    expect(decoded.errors).toEqual(["first", "second", "third"]);
  });

  it("round-trips an empty message", () => {
    const decoded = decodeMessage(encodeMessage(emptyMessage()));
    expect(decoded).toEqual(emptyMessage());
  });

  it("round-trips an empty payload alongside other fields", () => {
    const decoded = decodeMessage(
      encodeMessage(
        emptyMessage({
          requestId: 7,
          query: "{ users { id } }",
        })
      )
    );
    expect(decoded.requestId).toBe(7);
    expect(decoded.query).toBe("{ users { id } }");
    expect(decoded.payload.length).toBe(0);
    expect(decoded.errors).toEqual([]);
  });

  it("round-trips multiple errors as a repeated field", () => {
    const decoded = decodeMessage(
      encodeMessage(
        emptyMessage({
          requestId: 1,
          type: BifrostMessageType.Error,
          errors: ["validation failed", "missing field: id", "row not found"],
        })
      )
    );
    expect(decoded.errors).toEqual([
      "validation failed",
      "missing field: id",
      "row not found",
    ]);
    expect(decoded.type).toBe(BifrostMessageType.Error);
  });

  it.each([
    BifrostMessageType.Query,
    BifrostMessageType.Mutation,
    BifrostMessageType.Result,
    BifrostMessageType.Error,
  ])("round-trips message type %i", (type) => {
    // requestId set to 1 so the type field actually serializes (default is Query/0)
    const decoded = decodeMessage(
      encodeMessage(emptyMessage({ requestId: 1, type }))
    );
    expect(decoded.type).toBe(type);
  });

  it("skips unknown fields", () => {
    // Manually construct a buffer containing field 99 (varint) followed by field 1.
    // Tag for field 99, wire type 0 (varint): (99 << 3) | 0 = 792 → varint encoding.
    const tag99 = 99 << 3;
    const tagBytes: number[] = [];
    let v = tag99;
    while (v >= 0x80) {
      tagBytes.push((v & 0x7f) | 0x80);
      v >>>= 7;
    }
    tagBytes.push(v);
    // Followed by varint value 0
    const unknownField = new Uint8Array([...tagBytes, 0]);

    // A normal encoded message with requestId=5
    const known = encodeMessage(emptyMessage({ requestId: 5 }));

    const combined = new Uint8Array(unknownField.length + known.length);
    combined.set(unknownField, 0);
    combined.set(known, unknownField.length);

    const decoded = decodeMessage(combined);
    expect(decoded.requestId).toBe(5);
  });
});

describe("BifrostBinaryClient", () => {
  let tracker: FakeWebSocketTracker;
  let client: BifrostBinaryClient;

  beforeEach(() => {
    tracker = createFakeWebSocketConstructor();
    client = new BifrostBinaryClient({
      url: TEST_URL,
      WebSocket: tracker.ctor,
      requestTimeoutMs: 5_000,
    });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  describe("constructor", () => {
    it("throws if no WebSocket constructor is available", () => {
      const original = (
        globalThis as unknown as { WebSocket?: unknown }
      ).WebSocket;
      try {
        delete (globalThis as { WebSocket?: unknown }).WebSocket;
        expect(
          () => new BifrostBinaryClient({ url: TEST_URL })
        ).toThrowError(/No WebSocket implementation available/);
      } finally {
        if (original !== undefined) {
          (globalThis as { WebSocket?: unknown }).WebSocket = original;
        }
      }
    });
  });

  describe("connect()", () => {
    it("resolves when onopen fires", async () => {
      const onOpen = vi.fn();
      const c = new BifrostBinaryClient({
        url: TEST_URL,
        WebSocket: tracker.ctor,
        onOpen,
      });

      const p = c.connect();
      expect(tracker.instances).toHaveLength(1);
      expect(tracker.last.binaryType).toBe("arraybuffer");
      expect(tracker.last.url).toBe(TEST_URL);

      tracker.last.simulateOpen();
      await expect(p).resolves.toBeUndefined();
      expect(onOpen).toHaveBeenCalledOnce();
      expect(c.connected).toBe(true);
    });

    it("rejects when onerror fires before the connection opens", async () => {
      const onError = vi.fn();
      const c = new BifrostBinaryClient({
        url: TEST_URL,
        WebSocket: tracker.ctor,
        onError,
      });

      const p = c.connect();
      tracker.last.simulateError();
      await expect(p).rejects.toThrowError("WebSocket connection failed");
      expect(onError).toHaveBeenCalledOnce();
      expect(c.connected).toBe(false);
    });

    it("resolves immediately when already connected", async () => {
      const p1 = client.connect();
      tracker.last.simulateOpen();
      await p1;

      // Second connect call should not create a new socket.
      const before = tracker.instances.length;
      await expect(client.connect()).resolves.toBeUndefined();
      expect(tracker.instances.length).toBe(before);
    });
  });

  describe("query() and mutate()", () => {
    beforeEach(async () => {
      const p = client.connect();
      tracker.last.simulateOpen();
      await p;
    });

    it("rejects query() when not connected", async () => {
      const fresh = new BifrostBinaryClient({
        url: TEST_URL,
        WebSocket: tracker.ctor,
      });
      await expect(fresh.query("{ x }")).rejects.toThrowError("Not connected");
    });

    it("encodes a query frame and uses requestId starting at 1", async () => {
      const ws = tracker.last;
      void client.query("{ users { id name } }", { limit: 10 });

      expect(ws.sentFrames).toHaveLength(1);
      const decoded = decodeMessage(ws.sentFrames[0]!);
      expect(decoded.requestId).toBe(1);
      expect(decoded.type).toBe(BifrostMessageType.Query);
      expect(decoded.query).toBe("{ users { id name } }");
      expect(decoded.variablesJson).toBe(JSON.stringify({ limit: 10 }));
      expect(decoded.payload.length).toBe(0);
      expect(decoded.errors).toEqual([]);
    });

    it("omits variablesJson when no variables are provided", async () => {
      const ws = tracker.last;
      void client.query("{ users { id } }");
      const decoded = decodeMessage(ws.sentFrames[0]!);
      expect(decoded.variablesJson).toBe("");
    });

    it("encodes a mutation frame with type=Mutation", async () => {
      const ws = tracker.last;
      void client.mutate("mutation { insert_users(input: {name: \"Bob\"}) { id } }");

      const decoded = decodeMessage(ws.sentFrames[0]!);
      expect(decoded.type).toBe(BifrostMessageType.Mutation);
      expect(decoded.requestId).toBe(1);
    });

    it("uses monotonically increasing requestIds", async () => {
      const ws = tracker.last;
      void client.query("{ a { id } }");
      void client.query("{ b { id } }");
      void client.mutate("mutation { c { id } }");

      const ids = ws.sentFrames.map((f) => decodeMessage(f).requestId);
      expect(ids).toEqual([1, 2, 3]);
      expect(client.pendingCount).toBe(3);
    });

    it("resolves with parsed payload data and empty errors array", async () => {
      const ws = tracker.last;
      const promise = client.query("{ users { id } }");
      const sentId = decodeMessage(ws.sentFrames[0]!).requestId;

      ws.receive(
        encodeMessage(
          emptyMessage({
            requestId: sentId,
            type: BifrostMessageType.Result,
            payload: jsonPayload({ users: [{ id: "u1" }] }),
          })
        )
      );

      const result = await promise;
      expect(result.errors).toEqual([]);
      expect(result.data).toEqual({ users: [{ id: "u1" }] });
      expect(client.pendingCount).toBe(0);
    });

    it("resolves with null data when payload is empty", async () => {
      const ws = tracker.last;
      const promise = client.query("{ noop }");
      const sentId = decodeMessage(ws.sentFrames[0]!).requestId;

      ws.receive(
        encodeMessage(
          emptyMessage({
            requestId: sentId,
            type: BifrostMessageType.Result,
          })
        )
      );

      const result = await promise;
      expect(result.data).toBeNull();
      expect(result.errors).toEqual([]);
    });

    it("surfaces server errors in the errors array", async () => {
      const ws = tracker.last;
      const promise = client.query("{ broken }");
      const sentId = decodeMessage(ws.sentFrames[0]!).requestId;

      ws.receive(
        encodeMessage(
          emptyMessage({
            requestId: sentId,
            type: BifrostMessageType.Error,
            errors: ["syntax error", "unexpected token"],
          })
        )
      );

      const result = await promise;
      expect(result.data).toBeNull();
      expect(result.errors).toEqual(["syntax error", "unexpected token"]);
      expect(client.pendingCount).toBe(0);
    });

    it("multiplexes: two in-flight requests resolve in arbitrary order", async () => {
      const ws = tracker.last;
      const p1 = client.query("{ a }");
      const p2 = client.query("{ b }");

      const id1 = decodeMessage(ws.sentFrames[0]!).requestId;
      const id2 = decodeMessage(ws.sentFrames[1]!).requestId;
      expect(id1).toBe(1);
      expect(id2).toBe(2);
      expect(client.pendingCount).toBe(2);

      // Respond to second request first.
      ws.receive(
        encodeMessage(
          emptyMessage({
            requestId: id2,
            type: BifrostMessageType.Result,
            payload: jsonPayload({ tag: "second" }),
          })
        )
      );

      // Then the first.
      ws.receive(
        encodeMessage(
          emptyMessage({
            requestId: id1,
            type: BifrostMessageType.Result,
            payload: jsonPayload({ tag: "first" }),
          })
        )
      );

      const [r1, r2] = await Promise.all([p1, p2]);
      expect(r1.data).toEqual({ tag: "first" });
      expect(r2.data).toEqual({ tag: "second" });
      expect(client.pendingCount).toBe(0);
    });

    it("ignores response frames with unknown requestId", async () => {
      const ws = tracker.last;
      const promise = client.query("{ a }");
      const sentId = decodeMessage(ws.sentFrames[0]!).requestId;

      // Wrong requestId — should be silently ignored.
      ws.receive(
        encodeMessage(
          emptyMessage({
            requestId: sentId + 999,
            type: BifrostMessageType.Result,
            payload: jsonPayload({ ignored: true }),
          })
        )
      );
      expect(client.pendingCount).toBe(1);

      // Correct response still resolves.
      ws.receive(
        encodeMessage(
          emptyMessage({
            requestId: sentId,
            type: BifrostMessageType.Result,
            payload: jsonPayload({ ok: true }),
          })
        )
      );

      const result = await promise;
      expect(result.data).toEqual({ ok: true });
    });

    it("forwards decode errors to onError without affecting pending requests", async () => {
      const onError = vi.fn();
      const c = new BifrostBinaryClient({
        url: TEST_URL,
        WebSocket: tracker.ctor,
        onError,
      });
      const cp = c.connect();
      tracker.last.simulateOpen();
      await cp;

      const ws = tracker.last;
      void c.query("{ x }");

      // Bytes containing a tag with field 1 (request_id, varint) but no value
      // following — the BinaryReader will throw when it tries to read the varint
      // past the end of the buffer.
      const tagOnly = new Uint8Array([(1 << 3) | 0]); // tag for field 1, wire type 0
      ws.receive(tagOnly);

      expect(onError).toHaveBeenCalledOnce();
      expect(c.pendingCount).toBe(1); // pending request not affected
    });
  });

  describe("timeout", () => {
    beforeEach(async () => {
      vi.useFakeTimers();
      const p = client.connect();
      tracker.last.simulateOpen();
      await p;
    });

    it("rejects pending request after requestTimeoutMs and clears pendingCount", async () => {
      const promise = client.query("{ slow }");
      expect(client.pendingCount).toBe(1);

      const assertion = expect(promise).rejects.toThrowError(
        /Request 1 timed out/
      );
      await vi.advanceTimersByTimeAsync(5_000);
      await assertion;

      expect(client.pendingCount).toBe(0);
    });

    it("only the timed-out request rejects when others are still in flight", async () => {
      const slow = client.query("{ slow }");
      // Advance partway, then start a second request with a fresh timer.
      await vi.advanceTimersByTimeAsync(2_000);
      const fast = client.query("{ fast }");

      // Respond to the fast request immediately.
      const ws = tracker.last;
      const fastId = decodeMessage(ws.sentFrames[1]!).requestId;
      ws.receive(
        encodeMessage(
          emptyMessage({
            requestId: fastId,
            type: BifrostMessageType.Result,
            payload: jsonPayload({ done: true }),
          })
        )
      );
      await expect(fast).resolves.toMatchObject({ data: { done: true } });

      // Now expire the slow request.
      const slowAssertion = expect(slow).rejects.toThrowError(/timed out/);
      await vi.advanceTimersByTimeAsync(3_000);
      await slowAssertion;
      expect(client.pendingCount).toBe(0);
    });
  });

  describe("close()", () => {
    beforeEach(async () => {
      const p = client.connect();
      tracker.last.simulateOpen();
      await p;
    });

    it("rejects all pending requests when the connection closes", async () => {
      const p1 = client.query("{ a }");
      const p2 = client.query("{ b }");
      expect(client.pendingCount).toBe(2);

      const a1 = expect(p1).rejects.toThrowError(/Connection closed/);
      const a2 = expect(p2).rejects.toThrowError(/Connection closed/);

      client.close();
      await Promise.all([a1, a2]);

      expect(client.pendingCount).toBe(0);
      expect(client.connected).toBe(false);
      expect(tracker.last.closeCalls).toEqual([
        { code: 1000, reason: "Client closed" },
      ]);
    });

    it("rejects pending requests when the server initiates close", async () => {
      const p = client.query("{ a }");
      const assertion = expect(p).rejects.toThrowError(
        /Connection closed: 1011 server error/
      );
      tracker.last.simulateClose(1011, "server error");
      await assertion;
      expect(client.pendingCount).toBe(0);
    });

    it("invokes the onClose callback with code and reason", async () => {
      const onClose = vi.fn();
      const c = new BifrostBinaryClient({
        url: TEST_URL,
        WebSocket: tracker.ctor,
        onClose,
      });
      const cp = c.connect();
      tracker.last.simulateOpen();
      await cp;

      tracker.last.simulateClose(1006, "abnormal");
      expect(onClose).toHaveBeenCalledWith(1006, "abnormal");
    });

    it("is a no-op when called with no active connection", () => {
      const fresh = new BifrostBinaryClient({
        url: TEST_URL,
        WebSocket: tracker.ctor,
      });
      expect(() => fresh.close()).not.toThrow();
    });
  });
});

describe("FakeWebSocket constructor static OPEN", () => {
  it("exposes OPEN matching the IWebSocketConstructor contract", () => {
    const tracker = createFakeWebSocketConstructor();
    expect(tracker.ctor.OPEN).toBe(FakeWebSocketReadyState.OPEN);
    const ws = new tracker.ctor("ws://example.invalid");
    expect(ws).toBeInstanceOf(FakeWebSocket);
    expect(tracker.instances).toContain(ws);
    expect(tracker.last).toBe(ws);
  });
});
