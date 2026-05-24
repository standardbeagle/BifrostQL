/**
 * Unit tests for the raw-SQL bridge wrapper.
 *
 * Like native-bridge.test.ts, these install a fake `window.external` and
 * capture the single receiveMessage dispatch so each test can simulate the
 * host's `exec-sql` reply synchronously. The bridge modules are singletons,
 * so each test re-imports via `vi.resetModules()` for clean isolation.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

interface FakeExternal {
  sendMessage: ReturnType<typeof vi.fn>;
  receiveMessage: ReturnType<typeof vi.fn>;
  dispatch: ((json: string) => void) | null;
}

function installFakeExternal(): FakeExternal {
  const fake: FakeExternal = {
    sendMessage: vi.fn(),
    receiveMessage: vi.fn(),
    dispatch: null,
  };
  fake.receiveMessage.mockImplementation((cb: (json: string) => void) => {
    fake.dispatch = cb;
  });
  (globalThis as unknown as { window: { external: FakeExternal } }).window = {
    external: fake,
  };
  return fake;
}

function clearExternal(): void {
  delete (globalThis as unknown as { window?: unknown }).window;
}

function lastSent(fake: FakeExternal): { id: string; kind: string; payload: unknown } {
  const calls = fake.sendMessage.mock.calls;
  expect(calls.length).toBeGreaterThan(0);
  return JSON.parse(calls[calls.length - 1][0] as string);
}

async function loadSqlBridge(): Promise<typeof import("./sql-bridge")> {
  vi.resetModules();
  return import("./sql-bridge");
}

describe("sql-bridge", () => {
  beforeEach(() => {
    clearExternal();
  });
  afterEach(() => {
    clearExternal();
    vi.restoreAllMocks();
  });

  it("isSqlBridgeAvailable is false without a Photino external", async () => {
    const { isSqlBridgeAvailable } = await loadSqlBridge();
    expect(isSqlBridgeAvailable()).toBe(false);
  });

  it("isSqlBridgeAvailable is true with a Photino external", async () => {
    installFakeExternal();
    const { isSqlBridgeAvailable } = await loadSqlBridge();
    expect(isSqlBridgeAvailable()).toBe(true);
  });

  it("execSql sends an exec-sql envelope and resolves the result", async () => {
    const fake = installFakeExternal();
    const { execSql } = await loadSqlBridge();

    const promise = execSql("SELECT 1", { maxRows: 50, timeout: 5 });

    const sent = lastSent(fake);
    expect(sent.kind).toBe("exec-sql");
    expect(sent.payload).toMatchObject({ sql: "SELECT 1", maxRows: 50, timeout: 5 });

    const result = {
      columns: [{ name: "id", type: "int" }],
      rows: [[1]],
      rowsAffected: 0,
      truncated: false,
    };
    fake.dispatch!(JSON.stringify({ id: sent.id, kind: "result", payload: result }));

    await expect(promise).resolves.toEqual(result);
  });

  it("execSql forwards named parameters", async () => {
    const fake = installFakeExternal();
    const { execSql } = await loadSqlBridge();

    void execSql("SELECT * FROM t WHERE id = @id", { params: { id: 7 } });

    const sent = lastSent(fake);
    expect(sent.payload).toMatchObject({ params: { id: 7 } });
  });

  it("execSql rejects with a BridgeError on host error", async () => {
    const fake = installFakeExternal();
    const { execSql } = await loadSqlBridge();
    const { BridgeError } = await import("./native-bridge");

    const promise = execSql("DROP TABLE bad");
    const sent = lastSent(fake);
    fake.dispatch!(
      JSON.stringify({ id: sent.id, kind: "error", payload: { message: "no such table" } })
    );

    await expect(promise).rejects.toBeInstanceOf(BridgeError);
    await expect(promise).rejects.toThrow("no such table");
  });

  it("execSql rejects when the bridge is unavailable", async () => {
    const { execSql } = await loadSqlBridge();
    await expect(execSql("SELECT 1")).rejects.toThrow(/not available/i);
  });
});
