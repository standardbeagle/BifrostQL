/**
 * Unit tests for the visual query builder bridge wrapper. Installs a fake
 * `window.external`, captures the dispatch callback, and simulates the host's
 * `get-builder-schema` reply. Modules are singletons, so each test re-imports
 * via vi.resetModules() for isolation (same pattern as native-bridge.test.ts).
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

async function loadBuilderBridge(): Promise<typeof import("./builder-bridge")> {
  vi.resetModules();
  return import("./builder-bridge");
}

describe("builder-bridge", () => {
  beforeEach(() => clearExternal());
  afterEach(() => {
    clearExternal();
    vi.restoreAllMocks();
  });

  it("isBuilderBridgeAvailable reflects bridge presence", async () => {
    let mod = await loadBuilderBridge();
    expect(mod.isBuilderBridgeAvailable()).toBe(false);

    installFakeExternal();
    mod = await loadBuilderBridge();
    expect(mod.isBuilderBridgeAvailable()).toBe(true);
  });

  it("getBuilderSchema sends get-builder-schema and resolves the payload", async () => {
    const fake = installFakeExternal();
    const { getBuilderSchema } = await loadBuilderBridge();

    const promise = getBuilderSchema();

    const sent = lastSent(fake);
    expect(sent.kind).toBe("get-builder-schema");

    const schema = {
      tables: [{ schema: "dbo", name: "child", qualified: "dbo.child" }],
      columns: [
        { table: "dbo.child", name: "id", type: "int", nullable: false, isPrimaryKey: true },
      ],
      relationships: [
        {
          leftTable: "dbo.child",
          leftColumns: ["tenant_id", "parent_id"],
          rightTable: "dbo.parent",
          rightColumns: ["tenant_id", "id"],
        },
      ],
    };
    fake.dispatch!(JSON.stringify({ id: sent.id, kind: "result", payload: schema }));

    await expect(promise).resolves.toEqual(schema);
  });

  it("getBuilderSchema rejects when the bridge is unavailable", async () => {
    const { getBuilderSchema } = await loadBuilderBridge();
    await expect(getBuilderSchema()).rejects.toThrow(/not available/i);
  });

  it("getBuilderSchema surfaces host errors as BridgeError", async () => {
    const fake = installFakeExternal();
    const { getBuilderSchema } = await loadBuilderBridge();
    const { BridgeError } = await import("./native-bridge");

    const promise = getBuilderSchema();
    const sent = lastSent(fake);
    fake.dispatch!(
      JSON.stringify({ id: sent.id, kind: "error", payload: { message: "No active database connection." } })
    );

    await expect(promise).rejects.toBeInstanceOf(BridgeError);
    await expect(promise).rejects.toThrow(/no active database/i);
  });

  const sampleSpec = {
    tables: [{ table: "users" }],
    columns: [{ table: "users", column: "id", show: true, sort: "none" as const }],
    joins: [],
  };

  it("buildSql sends build-sql with the spec and resolves sql + parameters", async () => {
    const fake = installFakeExternal();
    const { buildSql } = await loadBuilderBridge();

    const promise = buildSql(sampleSpec);

    const sent = lastSent(fake);
    expect(sent.kind).toBe("build-sql");
    expect(sent.payload).toMatchObject({ tables: [{ table: "users" }] });

    const result = { sql: "SELECT [t0].[id] FROM ...", parameters: {} };
    fake.dispatch!(JSON.stringify({ id: sent.id, kind: "result", payload: result }));

    await expect(promise).resolves.toEqual(result);
  });

  it("buildAndExec sends build-and-exec and resolves the columnar result", async () => {
    const fake = installFakeExternal();
    const { buildAndExec } = await loadBuilderBridge();

    const promise = buildAndExec(sampleSpec);

    const sent = lastSent(fake);
    expect(sent.kind).toBe("build-and-exec");

    const result = {
      sql: "SELECT ...",
      columns: [{ name: "id", type: "INTEGER" }],
      rows: [[1]],
      rowsAffected: 0,
      truncated: false,
    };
    fake.dispatch!(JSON.stringify({ id: sent.id, kind: "result", payload: result }));

    await expect(promise).resolves.toEqual(result);
  });
});
