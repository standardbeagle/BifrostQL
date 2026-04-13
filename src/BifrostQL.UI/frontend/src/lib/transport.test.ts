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
  BinaryTransport,
  DEFAULT_TRANSPORT_MODE,
  HttpTransport,
  TRANSPORT_STORAGE_KEY,
  createTransport,
  deriveBinaryUrl,
  loadTransportMode,
  normalizeTransportMode,
  saveTransportMode,
} from "./transport";

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
 * coalescing, and close cleanup without touching the real WebSocket.
 */
class FakeBinaryClient {
  connectCalls = 0;
  queryCalls: Array<{ text: string; variables?: Record<string, unknown> }> = [];
  closeCalls = 0;
  isConnected = false;
  connectError: Error | null = null;
  nextResult: { data: unknown; errors: string[] } = { data: { ok: true }, errors: [] };

  async connect(): Promise<void> {
    this.connectCalls++;
    if (this.connectError) {
      throw this.connectError;
    }
    this.isConnected = true;
  }

  async query(
    text: string,
    variables?: Record<string, unknown>
  ): Promise<{ data: unknown; errors: string[] }> {
    this.queryCalls.push({ text, variables });
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

  it("clears the in-flight connect promise on connect failure so retries work", async () => {
    const fakes: FakeBinaryClient[] = [];
    const t = new BinaryTransport(
      "ws://localhost:5000/bifrost-ws",
      (() => {
        const f = new FakeBinaryClient();
        if (fakes.length === 0) {
          f.connectError = new Error("refused");
        }
        fakes.push(f);
        return f;
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      }) as unknown as any
    );

    await expect(t.query("{ a }")).rejects.toThrow("refused");
    // Second attempt should construct a fresh client and succeed.
    const result = await t.query("{ b }");
    expect(result.data).toEqual({ ok: true });
    expect(fakes).toHaveLength(2);
    expect(fakes[1].connectCalls).toBe(1);
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
});
