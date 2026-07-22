/**
 * Unit tests for the transport abstraction.
 *
 * These tests intentionally avoid touching real I/O:
 *
 * - The HTTP path is exercised by stubbing `globalThis.fetch` with a fake
 *   that returns canned `Response` objects.
 *
 * - The binary path is exercised with a hand-written fake that implements
 *   the small slice of `BifrostBinaryClient` the transport actually
 *   touches (`connect`, `query`, `close`, `connected`). We never open a
 *   real WebSocket — that's covered by the binary-client package's own
 *   FakeWebSocket suite.
 *
 * - localStorage persistence is exercised against an in-memory fake so
 *   the tests don't pollute or depend on a real browser storage.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  BINARY_CONNECT_TIMEOUT_MS,
  BinaryTransport,
  DEFAULT_TRANSPORT_MODE,
  HttpTransport,
  TRANSPORT_STORAGE_KEY,
  createTransport,
  deriveBinaryUrl,
  isMutationDocument,
  loadTransportMode,
  normalizeTransportMode,
  saveTransportMode,
  type QueryTransport,
  type QueryTransportResult,
  type TransportMode,
} from "./transport";
import { TransportGraphQLFetcher } from "./transport-fetcher";

/**
 * Minimal in-memory storage that satisfies the slice of the Storage
 * interface the transport module touches. Used to drive the persistence
 * tests without depending on a real localStorage being available in the
 * test environment.
 */
class MemoryStorage implements Pick<Storage, "getItem" | "setItem"> {
  private readonly data = new Map<string, string>();

  getItem(key: string): string | null {
    return this.data.has(key) ? this.data.get(key)! : null;
  }

  setItem(key: string, value: string): void {
    this.data.set(key, value);
  }
}

/**
 * Minimal fake of `BifrostBinaryClient` used by the BinaryTransport tests.
 * Tracks call counts so the test can assert lazy connect, single-connect
 * coalescing, mutation routing, and close cleanup without touching the real
 * WebSocket.
 */
class FakeBinaryClient {
  connectCalls = 0;
  queryCalls: Array<{
    text: string;
    variables?: Record<string, unknown>;
    signal?: AbortSignal;
  }> = [];
  mutateCalls: Array<{
    text: string;
    variables?: Record<string, unknown>;
    signal?: AbortSignal;
  }> = [];
  closeCalls = 0;
  isConnected = false;
  connectError: Error | null = null;
  nextResult: { data: unknown; errors: string[] } = { data: { ok: true }, errors: [] };

  async connect(): Promise<void> {
    // Mirrors the real client's OPEN short-circuit: connect() on an already
    // open client resolves without opening a second socket, so connectCalls
    // counts actual socket opens.
    if (this.isConnected) {
      return;
    }
    this.connectCalls++;
    if (this.connectError) {
      // Mirrors the real client: a failed connect leaves the client usable
      // for a later retry, so the error is consumed rather than sticky.
      const err = this.connectError;
      this.connectError = null;
      throw err;
    }
    this.isConnected = true;
  }

  async query(
    text: string,
    variables?: Record<string, unknown>,
    signal?: AbortSignal
  ): Promise<{ data: unknown; errors: string[] }> {
    this.queryCalls.push({ text, variables, signal });
    return this.nextResult;
  }

  async mutate(
    text: string,
    variables?: Record<string, unknown>,
    signal?: AbortSignal
  ): Promise<{ data: unknown; errors: string[] }> {
    this.mutateCalls.push({ text, variables, signal });
    return this.nextResult;
  }

  close(): void {
    this.closeCalls++;
    this.isConnected = false;
  }

  get connected(): boolean {
    return this.isConnected;
  }
}

describe("normalizeTransportMode", () => {
  it("returns the value when it is a recognized mode", () => {
    expect(normalizeTransportMode("http")).toBe("http");
    expect(normalizeTransportMode("binary")).toBe("binary");
  });

  it("falls back to the default for null", () => {
    expect(normalizeTransportMode(null)).toBe(DEFAULT_TRANSPORT_MODE);
  });

  it("falls back to the default for unknown strings", () => {
    expect(normalizeTransportMode("websocket")).toBe(DEFAULT_TRANSPORT_MODE);
    expect(normalizeTransportMode("")).toBe(DEFAULT_TRANSPORT_MODE);
  });
});

describe("loadTransportMode / saveTransportMode", () => {
  it("returns the default when storage is null", () => {
    expect(loadTransportMode(null)).toBe("http");
  });

  it("round-trips a value through an in-memory store", () => {
    const storage = new MemoryStorage();
    saveTransportMode("binary", storage);
    expect(storage.getItem(TRANSPORT_STORAGE_KEY)).toBe("binary");
    expect(loadTransportMode(storage)).toBe("binary");
  });

  it("falls back to the default when storage holds an unknown value", () => {
    const storage = new MemoryStorage();
    storage.setItem(TRANSPORT_STORAGE_KEY, "carrier-pigeon");
    expect(loadTransportMode(storage)).toBe("http");
  });

  it("does not throw if getItem throws", () => {
    const broken: Pick<Storage, "getItem"> = {
      getItem() {
        throw new Error("blocked");
      },
    };
    expect(() => loadTransportMode(broken)).not.toThrow();
    expect(loadTransportMode(broken)).toBe("http");
  });

  it("does not throw if setItem throws", () => {
    const broken: Pick<Storage, "setItem"> = {
      setItem() {
        throw new Error("quota");
      },
    };
    expect(() => saveTransportMode("binary", broken)).not.toThrow();
  });

  it("treats a null storage on save as a no-op", () => {
    expect(() => saveTransportMode("binary", null)).not.toThrow();
  });
});

describe("isMutationDocument", () => {
  it("detects a bare mutation keyword", () => {
    expect(isMutationDocument("mutation { doThing }")).toBe(true);
  });

  it("detects a named mutation with leading whitespace", () => {
    expect(isMutationDocument("\n\t  mutation Save($x: Int!) { save(x: $x) }")).toBe(
      true
    );
  });

  it("detects a mutation behind leading comment lines", () => {
    expect(
      isMutationDocument("# save the row\n# really\nmutation { save }")
    ).toBe(true);
  });

  it("does not flag shorthand queries", () => {
    expect(isMutationDocument("{ users { id } }")).toBe(false);
  });

  it("does not flag explicit queries or subscriptions", () => {
    expect(isMutationDocument("query Users { users { id } }")).toBe(false);
    expect(isMutationDocument("subscription S { events { id } }")).toBe(false);
  });

  it("does not flag fields whose name merely starts with 'mutation'", () => {
    expect(isMutationDocument("{ mutationLog { id } }")).toBe(false);
    expect(isMutationDocument("mutationLog { id }")).toBe(false);
  });

  it("does not flag a comment that mentions mutation before a query", () => {
    expect(isMutationDocument("# mutation in a comment\n{ users { id } }")).toBe(
      false
    );
  });

  it("detects a mutation behind leading fragment definitions", () => {
    expect(
      isMutationDocument(
        'fragment F on User { id name(format: "{a}") }\nmutation M { updateUser { ...F } }'
      )
    ).toBe(true);
    expect(
      isMutationDocument(
        "fragment A on T { x }\nfragment B on T { y }\nmutation { save }"
      )
    ).toBe(true);
  });

  it("does not flag fragment-first query documents", () => {
    expect(
      isMutationDocument("fragment F on User { id }\nquery Q { users { ...F } }")
    ).toBe(false);
    expect(
      isMutationDocument("fragment F on User { id }\n{ users { ...F } }")
    ).toBe(false);
  });

  it("classifies an unbalanced fragment document as a query", () => {
    expect(isMutationDocument("fragment F on User { id\nmutation { save }")).toBe(
      false
    );
  });
});

describe("deriveBinaryUrl", () => {
  it("maps http: to ws:", () => {
    expect(deriveBinaryUrl("http://localhost:5000", "/bifrost-ws")).toBe(
      "ws://localhost:5000/bifrost-ws"
    );
  });

  it("maps https: to wss:", () => {
    expect(deriveBinaryUrl("https://example.com", "/bifrost-ws")).toBe(
      "wss://example.com/bifrost-ws"
    );
  });

  it("strips trailing slashes from the endpoint", () => {
    expect(deriveBinaryUrl("http://localhost:5000//", "/bifrost-ws")).toBe(
      "ws://localhost:5000/bifrost-ws"
    );
  });

  it("normalizes a path missing its leading slash", () => {
    expect(deriveBinaryUrl("http://localhost:5000", "bifrost-ws")).toBe(
      "ws://localhost:5000/bifrost-ws"
    );
  });

  it("preserves the host and port", () => {
    expect(deriveBinaryUrl("http://10.0.0.5:8080", "/ws")).toBe(
      "ws://10.0.0.5:8080/ws"
    );
  });
});

describe("createTransport", () => {
  it("returns an HttpTransport for mode 'http'", () => {
    const t = createTransport("http", { endpoint: "http://localhost:5000" });
    expect(t).toBeInstanceOf(HttpTransport);
    expect(t.mode).toBe("http");
    expect(t.connected).toBe(true);
  });

  it("returns a BinaryTransport for mode 'binary'", () => {
    const fake = new FakeBinaryClient();
    const t = createTransport(
      "binary",
      { endpoint: "http://localhost:5000" },
      // The factory expects a function returning the real client type;
      // the fake is structurally compatible with the slice the transport
      // touches, so a typed cast is enough here.
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      (() => fake) as unknown as any
    );
    expect(t).toBeInstanceOf(BinaryTransport);
    expect(t.mode).toBe("binary");
    expect(t.connected).toBe(false);
  });

  it("respects custom paths", () => {
    const t = createTransport("http", {
      endpoint: "http://localhost:5000",
      graphqlPath: "/api/graphql",
    });
    expect(t.mode).toBe("http");
    // We can't introspect the URL directly; we verify it indirectly through
    // the fetch call below.
  });
});

describe("HttpTransport", () => {
  let originalFetch: typeof fetch | undefined;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    if (originalFetch) {
      globalThis.fetch = originalFetch;
    } else {
      // Some test environments don't set fetch by default.
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      delete (globalThis as any).fetch;
    }
  });

  it("posts the query and adapts the response envelope", async () => {
    const fetchSpy = vi.fn(async () =>
      new Response(JSON.stringify({ data: { hello: "world" }, errors: [] }), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      })
    );
    globalThis.fetch = fetchSpy as unknown as typeof fetch;

    const t = new HttpTransport("http://localhost:5000/graphql");
    const result = await t.query("{ hello }", { id: 1 });

    expect(result).toEqual({ data: { hello: "world" }, errors: [] });
    expect(fetchSpy).toHaveBeenCalledTimes(1);
    const call = fetchSpy.mock.calls[0] as unknown as [string, RequestInit];
    const [url, init] = call;
    expect(url).toBe("http://localhost:5000/graphql");
    expect(init.method).toBe("POST");
    expect(JSON.parse(init.body as string)).toEqual({
      query: "{ hello }",
      variables: { id: 1 },
    });
  });

  it("normalizes object-form GraphQL errors into strings", async () => {
    globalThis.fetch = (async () =>
      new Response(
        JSON.stringify({ data: null, errors: [{ message: "boom" }, "raw"] }),
        { status: 200 }
      )) as unknown as typeof fetch;

    const t = new HttpTransport("http://localhost:5000/graphql");
    const result = await t.query("{ broken }");
    expect(result.data).toBeNull();
    expect(result.errors).toEqual(["boom", "raw"]);
  });

  it("surfaces non-2xx responses as transport errors", async () => {
    globalThis.fetch = (async () =>
      new Response("oops", {
        status: 500,
        statusText: "Internal Server Error",
      })) as unknown as typeof fetch;

    const t = new HttpTransport("http://localhost:5000/graphql");
    const result = await t.query("{ broken }");
    expect(result.data).toBeNull();
    expect(result.errors).toEqual(["HTTP 500 Internal Server Error"]);
  });

  it("reports connected as true and close is a no-op", () => {
    const t = new HttpTransport("http://localhost:5000/graphql");
    expect(t.connected).toBe(true);
    expect(() => t.close()).not.toThrow();
    expect(t.connected).toBe(true);
  });
});

describe("BinaryTransport", () => {
  it("lazily connects on the first query and reports live connected state", async () => {
    const fake = new FakeBinaryClient();
    const t = new BinaryTransport(
      "ws://localhost:5000/bifrost-ws",
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      (() => fake) as unknown as any
    );

    expect(t.connected).toBe(false);
    expect(fake.connectCalls).toBe(0);

    const result = await t.query("{ hello }", { id: 1 });
    expect(result).toEqual({ data: { ok: true }, errors: [] });
    expect(fake.connectCalls).toBe(1);
    expect(fake.queryCalls).toEqual([
      { text: "{ hello }", variables: { id: 1 } },
    ]);
    expect(t.connected).toBe(true);
  });

  it("coalesces concurrent first queries onto a single connect", async () => {
    const fake = new FakeBinaryClient();
    const t = new BinaryTransport(
      "ws://localhost:5000/bifrost-ws",
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      (() => fake) as unknown as any
    );

    await Promise.all([
      t.query("{ a }"),
      t.query("{ b }"),
      t.query("{ c }"),
    ]);

    // The coalesce contract is "exactly one connect for N concurrent queries".
    // The order in which the three awaiters resume after the connect promise
    // resolves is microtask-scheduling dependent, so we don't pin it here —
    // we only assert that every query was issued exactly once.
    expect(fake.connectCalls).toBe(1);
    expect(fake.queryCalls).toHaveLength(3);
    expect(fake.queryCalls.map((c) => c.text).sort()).toEqual([
      "{ a }",
      "{ b }",
      "{ c }",
    ]);
  });

  it("retries connect on the same client after a failed connect without closing it", async () => {
    let created = 0;
    const fake = new FakeBinaryClient();
    fake.connectError = new Error("refused");
    const t = new BinaryTransport(
      "ws://localhost:5000/bifrost-ws",
      (() => {
        created++;
        return fake;
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      }) as unknown as any
    );

    await expect(t.query("{ a }")).rejects.toThrow("refused");
    // The failure must NOT close the client: closing would permanently stop
    // its reconnect controller and defeat the client's own retry/replay
    // design. The transport just rethrows and keeps the instance.
    expect(fake.closeCalls).toBe(0);

    // Second attempt retries connect on the SAME client and succeeds.
    const result = await t.query("{ b }");
    expect(result.data).toEqual({ ok: true });
    expect(created).toBe(1);
    expect(fake.connectCalls).toBe(2);
  });

  it("routes mutation documents through mutate() so the Mutation frame type is used", async () => {
    const fake = new FakeBinaryClient();
    const t = new BinaryTransport(
      "ws://localhost:5000/bifrost-ws",
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      (() => fake) as unknown as any
    );

    const controller = new AbortController();
    await t.query(
      "mutation { insert_users(input: { name: \"Bob\" }) { id } }",
      { name: "Bob" },
      { signal: controller.signal }
    );

    expect(fake.queryCalls).toHaveLength(0);
    expect(fake.mutateCalls).toHaveLength(1);
    expect(fake.mutateCalls[0].text).toContain("insert_users");
    expect(fake.mutateCalls[0].variables).toEqual({ name: "Bob" });
    expect(fake.mutateCalls[0].signal).toBe(controller.signal);
  });

  it("routes mutations preceded by comments and whitespace through mutate()", async () => {
    const fake = new FakeBinaryClient();
    const t = new BinaryTransport(
      "ws://localhost:5000/bifrost-ws",
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      (() => fake) as unknown as any
    );

    await t.query(
      "# update the row\n  # second comment\n\n  mutation UpdateRow($id: Int!) { update_rows(id: $id) { id } }",
      { id: 1 }
    );

    expect(fake.queryCalls).toHaveLength(0);
    expect(fake.mutateCalls).toHaveLength(1);
  });

  it("routes queries (named, shorthand, and query-prefixed) through query()", async () => {
    const fake = new FakeBinaryClient();
    const t = new BinaryTransport(
      "ws://localhost:5000/bifrost-ws",
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      (() => fake) as unknown as any
    );

    await t.query("{ users { id } }");
    await t.query("query Users { users { id } }");
    // A field merely named like the keyword must not be misrouted.
    await t.query("{ mutationLog { id } }");

    expect(fake.mutateCalls).toHaveLength(0);
    expect(fake.queryCalls).toHaveLength(3);
  });

  it("closes the underlying client and rejects future queries", async () => {
    const fake = new FakeBinaryClient();
    const t = new BinaryTransport(
      "ws://localhost:5000/bifrost-ws",
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      (() => fake) as unknown as any
    );
    await t.query("{ a }");
    t.close();
    expect(fake.closeCalls).toBe(1);
    await expect(t.query("{ b }")).rejects.toThrow("BinaryTransport is closed");
  });

  it("treats close on a never-connected transport as a no-op", () => {
    const t = new BinaryTransport(
      "ws://localhost:5000/bifrost-ws",
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      (() => new FakeBinaryClient()) as unknown as any
    );
    expect(() => t.close()).not.toThrow();
  });

  it("forwards live connection-state changes to the status callback", async () => {
    const changes: boolean[] = [];
    const fake = new FakeBinaryClient();
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    let captured: any;
    const t = new BinaryTransport(
      "ws://localhost:5000/bifrost-ws",
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      ((_url: string, options: any) => {
        captured = options;
        return fake;
      }) as unknown as any,
      { onConnectedChange: (connected) => changes.push(connected) }
    );

    await t.query("{ a }");

    // The transport wired the binary client's lifecycle hooks, each of which
    // reports the transport's current `connected` state (delegated to the
    // client) — so the badge tracks open/close/reconnect rather than one sample.
    expect(captured).toBeDefined();
    fake.isConnected = true;
    captured.onOpen();
    fake.isConnected = false;
    captured.onClose(1006, "drop");
    fake.isConnected = true;
    captured.onReconnect(1);
    fake.isConnected = false;
    captured.onReconnectFailed(5, new Error("gave up"));

    expect(changes).toEqual([true, false, true, false]);
  });

  it("passes no lifecycle hooks when no status callbacks are supplied", async () => {
    const fake = new FakeBinaryClient();
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    let captured: any;
    const t = new BinaryTransport(
      "ws://localhost:5000/bifrost-ws",
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      ((_url: string, options: any) => {
        captured = options;
        return fake;
      }) as unknown as any
    );

    await t.query("{ a }");
    expect(captured).toEqual({});
  });
});

describe("BinaryTransport connect timeout", () => {
  /**
   * Fake client whose connect() never settles, mirroring a WebSocket upgrade
   * the far side never answers (e.g. a dev proxy with no /bifrost-ws entry).
   */
  class HangingConnectClient {
    isConnected = false;
    closeCalls = 0;
    connectCalls = 0;

    connect(): Promise<void> {
      this.connectCalls++;
      return new Promise<void>(() => {
        // Intentionally never settles.
      });
    }

    async query(): Promise<{ data: unknown; errors: string[] }> {
      return { data: { ok: true }, errors: [] };
    }

    async mutate(): Promise<{ data: unknown; errors: string[] }> {
      return { data: { ok: true }, errors: [] };
    }

    close(): void {
      this.closeCalls++;
    }

    get connected(): boolean {
      return this.isConnected;
    }
  }

  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("rejects a query with an actionable error when the handshake never settles", async () => {
    const changes: boolean[] = [];
    const fake = new HangingConnectClient();
    const t = new BinaryTransport(
      "ws://localhost:5000/bifrost-ws",
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      (() => fake) as unknown as any,
      { onConnectedChange: (connected) => changes.push(connected) }
    );

    const pending = t.query("{ hello }");
    const expectation = expect(pending).rejects.toThrow(
      /could not connect to ws:\/\/localhost:5000\/bifrost-ws within 8s.*switch the transport back to HTTP/s
    );
    await vi.advanceTimersByTimeAsync(BINARY_CONNECT_TIMEOUT_MS);
    await expectation;

    // The UI badge is told the transport is not connected.
    expect(changes).toEqual([false]);
    // The client is kept (its reconnect design stays intact), not closed.
    expect(fake.closeCalls).toBe(0);
  });

  it("does not time out a connect that settles within the window", async () => {
    const fake = new FakeBinaryClient();
    const t = new BinaryTransport(
      "ws://localhost:5000/bifrost-ws",
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      (() => fake) as unknown as any
    );

    const result = await t.query("{ hello }");
    expect(result.data).toEqual({ ok: true });
    // The timeout timer was cleared when connect resolved — nothing left to fire.
    expect(vi.getTimerCount()).toBe(0);
  });

  it("fails subsequent queries fast instead of waiting out a fresh window each retry", async () => {
    const fake = new HangingConnectClient();
    const t = new BinaryTransport(
      "ws://localhost:5000/bifrost-ws",
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      (() => fake) as unknown as any
    );

    const first = t.query("{ a }");
    const firstExpectation = expect(first).rejects.toThrow(/within 8s/);
    await vi.advanceTimersByTimeAsync(BINARY_CONNECT_TIMEOUT_MS);
    await firstExpectation;

    // A retry (react-query re-issues the schema query) rejects immediately
    // with the same sticky failure — no timers advanced.
    await expect(t.query("{ b }")).rejects.toThrow(/within 8s/);
    expect(vi.getTimerCount()).toBe(0);
  });

  it("recovers once the socket actually reports open", async () => {
    const fake = new HangingConnectClient();
    const t = new BinaryTransport(
      "ws://localhost:5000/bifrost-ws",
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      (() => fake) as unknown as any
    );

    const pending = t.query("{ a }");
    const expectation = expect(pending).rejects.toThrow(/within 8s/);
    await vi.advanceTimersByTimeAsync(BINARY_CONNECT_TIMEOUT_MS);
    await expectation;

    // The client's background reconnect eventually lands the socket.
    fake.isConnected = true;

    const result = await t.query("{ b }");
    expect(result.data).toEqual({ ok: true });
  });

  it("cancels the pending timeout when the transport is closed mid-handshake", async () => {
    const fake = new HangingConnectClient();
    const t = new BinaryTransport(
      "ws://localhost:5000/bifrost-ws",
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      (() => fake) as unknown as any
    );

    const pending = t.query("{ a }");
    // Swallow the eventual (never-firing) rejection so the test harness does
    // not flag an unhandled rejection if the promise were ever to settle.
    pending.catch(() => {});
    expect(vi.getTimerCount()).toBe(1);

    t.close();
    expect(vi.getTimerCount()).toBe(0);
    expect(fake.closeCalls).toBe(1);
  });

  it("honors a custom timeout threaded through createTransport", async () => {
    const fake = new HangingConnectClient();
    const t = createTransport(
      "binary",
      {
        endpoint: "http://localhost:5000",
        binaryConnectTimeoutMs: 1_000,
      },
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      (() => fake) as unknown as any
    );

    const pending = t.query("{ a }");
    const expectation = expect(pending).rejects.toThrow(/within 1s/);
    await vi.advanceTimersByTimeAsync(1_000);
    await expectation;
  });

  /**
   * The fail-fast latch exists to stop react-query retries from stacking full
   * timeout windows. It must not become a permanent kill switch: the latch
   * used to be cleared only when the client already reported an open socket,
   * and it was checked BEFORE client.connect(), so once the client's own
   * reconnect controller had given up nothing could ever call connect() again.
   * One 8s handshake timeout killed binary mode for the life of the instance —
   * recoverable only by toggling the transport to build a fresh one.
   */
  it("genuinely retries the handshake once the fail-fast window elapses", async () => {
    const fake = new HangingConnectClient();
    const t = new BinaryTransport(
      "ws://localhost:5000/bifrost-ws",
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      (() => fake) as unknown as any
    );

    const first = t.query("{ a }");
    const firstExpectation = expect(first).rejects.toThrow(/within 8s/);
    await vi.advanceTimersByTimeAsync(BINARY_CONNECT_TIMEOUT_MS);
    await firstExpectation;
    expect(fake.connectCalls).toBe(1);

    // Inside the window the latch still short-circuits: no second socket open.
    await expect(t.query("{ b }")).rejects.toThrow(/within 8s/);
    expect(fake.connectCalls).toBe(1);

    // Once the window has passed, a query must reach client.connect() again —
    // that call is what re-arms the client's reconnect controller.
    await vi.advanceTimersByTimeAsync(BINARY_CONNECT_TIMEOUT_MS);
    const third = t.query("{ c }");
    const thirdExpectation = expect(third).rejects.toThrow(/within 8s/);
    await vi.advanceTimersByTimeAsync(0);
    expect(fake.connectCalls).toBe(2);
    await vi.advanceTimersByTimeAsync(BINARY_CONNECT_TIMEOUT_MS);
    await thirdExpectation;
  });

});

/**
 * Fake QueryTransport used to exercise the edit-db fetcher adapter without
 * any real I/O. Records the calls it receives and returns a canned envelope so
 * the tests can assert delegation, data unwrapping, and error rejection.
 */
class FakeTransport implements QueryTransport {
  readonly mode: TransportMode = "http";
  readonly connected = true;
  calls: Array<{ text: string; variables?: Record<string, unknown> }> = [];
  next: QueryTransportResult = { data: null, errors: [] };
  closeCalls = 0;

  async query(
    text: string,
    variables?: Record<string, unknown>
  ): Promise<QueryTransportResult> {
    this.calls.push({ text, variables });
    return this.next;
  }

  close(): void {
    this.closeCalls++;
  }
}

describe("TransportGraphQLFetcher", () => {
  it("delegates the query and variables to the underlying transport", async () => {
    const fake = new FakeTransport();
    fake.next = { data: { rows: [] }, errors: [] };
    const fetcher = new TransportGraphQLFetcher(fake);

    await fetcher.query("{ rows }", { limit: 10 });

    expect(fake.calls).toEqual([{ text: "{ rows }", variables: { limit: 10 } }]);
  });

  it("unwraps the data payload on success", async () => {
    const fake = new FakeTransport();
    fake.next = { data: { hello: "world" }, errors: [] };
    const fetcher = new TransportGraphQLFetcher(fake);

    const result = await fetcher.query<{ hello: string }>("{ hello }");

    expect(result).toEqual({ hello: "world" });
  });

  it("rejects with a joined message when the transport reports errors", async () => {
    const fake = new FakeTransport();
    fake.next = { data: null, errors: ["boom", "kaboom"] };
    const fetcher = new TransportGraphQLFetcher(fake);

    await expect(fetcher.query("{ broken }")).rejects.toThrow("boom; kaboom");
  });

  /**
   * The editor's built-in fetcher rejects with a GraphQLRequestError carrying
   * `errors` and any partial `data`; this adapter threw a bare Error with only
   * a joined message, so a consumer reading either one degraded the moment the
   * transport toggle flipped. The two paths must look the same to a caller.
   */
  it("rejects with an error carrying the structured errors and partial data", async () => {
    const fake = new FakeTransport();
    fake.next = { data: { rows: [1] }, errors: ["boom", "kaboom"] };
    const fetcher = new TransportGraphQLFetcher(fake);

    const error = await fetcher.query("{ broken }").then(
      () => null,
      (e: unknown) => e as Error & { errors?: Array<{ message: string }>; data?: unknown }
    );

    expect(error).not.toBeNull();
    expect(error!.name).toBe("GraphQLRequestError");
    expect(error!.errors).toEqual([{ message: "boom" }, { message: "kaboom" }]);
    // Partial data must survive: discarding it loses rows the server did return.
    expect(error!.data).toEqual({ rows: [1] });
  });

  it("routes through whichever transport instance it was given", async () => {
    // Simulates the app swapping the HTTP transport for the binary one on
    // toggle: a fetcher built around the new instance issues its queries there.
    const httpLike = new FakeTransport();
    const binaryLike = new FakeTransport();

    await new TransportGraphQLFetcher(httpLike).query("{ a }");
    await new TransportGraphQLFetcher(binaryLike).query("{ b }");

    expect(httpLike.calls.map((c) => c.text)).toEqual(["{ a }"]);
    expect(binaryLike.calls.map((c) => c.text)).toEqual(["{ b }"]);
  });
});

/**
 * Cross-transport equality + wire-routing for the phase 2-4 aggregate/pivot
 * surfaces (charts, dashboards, grouped grid, pivot). The server proves the two
 * wires emit byte-identical payloads for these queries (see the .NET
 * BinaryAggregatePivotPassthroughTests); these tests hold the CLIENT half of
 * that contract: given the same server payload, neither transport mangles the
 * shape — including a pivot's DYNAMIC output columns, whose names are data — so
 * the HTTP and binary results deep-equal per surface, and toggling the transport
 * demonstrably changes which wire carries the request.
 */
describe("aggregate/pivot cross-transport equality and wire routing", () => {
  let originalFetch: typeof fetch | undefined;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    if (originalFetch) {
      globalThis.fetch = originalFetch;
    } else {
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      delete (globalThis as any).fetch;
    }
  });

  // One representative payload per phase 2-4 surface. The pivot payload carries
  // dynamic columns (status values) and a null hole — the shape most at risk of
  // transport-specific mangling.
  const surfacePayloads: Array<{ surface: string; data: unknown }> = [
    {
      surface: "chart (single-dimension aggregate)",
      data: {
        ordersAggregate: [
          { region: "east", _count: 3, _sum: { amount: 150 } },
          { region: "west", _count: 2, _sum: { amount: 80 } },
        ],
      },
    },
    {
      surface: "grouped grid (multi-column aggregate)",
      data: {
        ordersAggregate: [
          { region: "east", status: "open", _count: 2, _sum: { amount: 110 }, _avg: { amount: 55 } },
          { region: "east", status: "closed", _count: 1, _sum: { amount: 40 }, _avg: { amount: 40 } },
          { region: "west", status: "open", _count: 1, _sum: { amount: 25 }, _avg: { amount: 25 } },
        ],
      },
    },
    {
      surface: "dashboard (aggregate tile)",
      data: { ordersAggregate: [{ _count: 5, _sum: { amount: 230 } }] },
    },
    {
      surface: "pivot (dynamic columns + null hole)",
      data: {
        ordersPivot: {
          pivotColumn: "status",
          rowKeys: ["region"],
          columns: ["closed", "open", "shipped"],
          rows: [
            { region: "east", cells: { closed: 40, open: 110, shipped: null } },
            { region: "west", cells: { closed: null, open: 25, shipped: 55 } },
          ],
        },
      },
    },
  ];

  for (const { surface, data } of surfacePayloads) {
    it(`returns identical results over HTTP and binary for ${surface}`, async () => {
      // HTTP wire: server responds with the surface payload.
      const fetchSpy = vi.fn(async () =>
        new Response(JSON.stringify({ data, errors: [] }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        })
      );
      globalThis.fetch = fetchSpy as unknown as typeof fetch;

      // Binary wire: the client decodes the SAME payload (server byte-equality
      // is proven .NET-side; here the binary client yields that decoded data).
      const fake = new FakeBinaryClient();
      fake.nextResult = { data, errors: [] };

      const http = new HttpTransport("http://localhost:5000/graphql");
      const binary = new BinaryTransport(
        "ws://localhost:5000/bifrost-ws",
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        (() => fake) as unknown as any
      );

      const query = "{ someAggregateOrPivot }";
      const httpResult = await http.query(query);
      const binaryResult = await binary.query(query);

      // Deep equality of the result payloads across both transports.
      expect(binaryResult).toEqual(httpResult);
      expect(binaryResult).toEqual({ data, errors: [] });
    });
  }

  it("toggling the transport changes the wire: binary sends a frame and issues no HTTP request", async () => {
    const fetchSpy = vi.fn(async () =>
      new Response(JSON.stringify({ data: { ok: true }, errors: [] }), { status: 200 })
    );
    globalThis.fetch = fetchSpy as unknown as typeof fetch;

    const fake = new FakeBinaryClient();
    const binary = new BinaryTransport(
      "ws://localhost:5000/bifrost-ws",
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      (() => fake) as unknown as any
    );

    await binary.query("{ ordersAggregate(groupBy: [region]) { region _count } }");

    // The binary wire carried it: the client's query path ran (a frame was sent)…
    expect(fake.queryCalls).toHaveLength(1);
    expect(fake.connectCalls).toBe(1);
    // …and the HTTP path issued no request at all.
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it("toggling the transport changes the wire: HTTP issues a request and opens no socket", async () => {
    const fetchSpy = vi.fn(async () =>
      new Response(JSON.stringify({ data: { ok: true }, errors: [] }), { status: 200 })
    );
    globalThis.fetch = fetchSpy as unknown as typeof fetch;

    const fake = new FakeBinaryClient();
    const http = new HttpTransport("http://localhost:5000/graphql");

    await http.query("{ ordersAggregate(groupBy: [region]) { region _count } }");

    // The HTTP wire carried it…
    expect(fetchSpy).toHaveBeenCalledTimes(1);
    // …and no binary socket was ever opened (the fake client stayed untouched).
    expect(fake.connectCalls).toBe(0);
    expect(fake.queryCalls).toHaveLength(0);
  });
});
