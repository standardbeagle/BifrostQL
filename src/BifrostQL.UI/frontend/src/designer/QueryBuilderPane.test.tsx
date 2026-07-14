// @vitest-environment jsdom
/**
 * Component tests for the saved-query surface of the designer pane — the
 * persistence path, where the non-destructive invariants can actually break:
 * rename must keep the id and store the definition it was given, delete must
 * only remove on a confirmed prompt, a drifted design must not be writable back
 * to the store, and an open request must not silently discard unsaved edits.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { SavedObject } from "@standardbeagle/edit-db";
import type { BuilderSchema } from "../lib/builder-bridge";
import {
  addTable,
  addJoin,
  toggleColumnShow,
  emptyDesignerState,
  type DesignerState,
} from "./designer-state";
import { serializeQuery } from "./saved-query";

const bridge = vi.hoisted(() => ({
  isBuilderBridgeAvailable: vi.fn(() => true),
  getBuilderSchema: vi.fn(),
  buildSql: vi.fn(),
  buildAndExec: vi.fn(),
}));
const store = vi.hoisted(() => ({
  put: vi.fn(),
  remove: vi.fn(),
  list: vi.fn(),
  get: vi.fn(),
}));

vi.mock("../lib/builder-bridge", () => bridge);
vi.mock("./saved-query-store", () => ({
  savedQueryStore: store,
  SAVED_QUERY_TYPE: "query",
  newQueryId: () => "new-id",
}));

// Imported after the mocks so the pane picks them up.
const { QueryBuilderPane } = await import("./QueryBuilderPane");

/** Two tables; `orders.total` is the column the drift cases drop. */
function schema(): BuilderSchema {
  return {
    tables: [
      { schema: "dbo", name: "customers", qualified: "dbo.customers" },
      { schema: "dbo", name: "orders", qualified: "dbo.orders" },
    ],
    columns: [
      { table: "dbo.customers", name: "id", type: "int", nullable: false, isPrimaryKey: true },
      { table: "dbo.customers", name: "name", type: "varchar", nullable: true, isPrimaryKey: false },
      { table: "dbo.orders", name: "id", type: "int", nullable: false, isPrimaryKey: true },
      { table: "dbo.orders", name: "customer_id", type: "int", nullable: true, isPrimaryKey: false },
      { table: "dbo.orders", name: "total", type: "decimal", nullable: true, isPrimaryKey: false },
    ],
    relationships: [],
  };
}

function withoutOrdersTotal(): BuilderSchema {
  const live = schema();
  live.columns = live.columns.filter((c) => !(c.table === "dbo.orders" && c.name === "total"));
  return live;
}

/** customers ⟕ orders, showing customers.name and orders.total. */
function designedState(): DesignerState {
  let s = addTable(emptyDesignerState, "dbo.customers");
  s = addTable(s, "dbo.orders");
  s = addJoin(s, {
    leftTable: "dbo.customers",
    leftColumns: ["id"],
    rightTable: "dbo.orders",
    rightColumns: ["customer_id"],
    type: "inner",
  });
  s = toggleColumnShow(s, "dbo.customers", "name");
  s = toggleColumnShow(s, "dbo.orders", "total");
  return s;
}

function savedQuery(definition: unknown = serializeQuery(designedState())): SavedObject {
  return {
    id: "q-1",
    type: "query",
    name: "Orders by customer",
    definition,
    version: 3,
  } as SavedObject;
}

function button(name: RegExp | string): HTMLButtonElement {
  return screen.getByRole("button", { name }) as HTMLButtonElement;
}

/** Renders the pane with a query already open, as the shell does. */
async function openPane(query: SavedObject, live: BuilderSchema = schema()) {
  bridge.getBuilderSchema.mockResolvedValue(live);
  const onOpenHandled = vi.fn();
  const view = render(<QueryBuilderPane openRequest={query} onOpenHandled={onOpenHandled} />);
  await screen.findByText(query.name);
  return { ...view, onOpenHandled };
}

beforeEach(() => {
  vi.clearAllMocks();
  store.put.mockImplementation(async (o: SavedObject) => ({ ...o, version: o.version + 1 }));
  store.remove.mockResolvedValue(undefined);
  store.list.mockResolvedValue([]);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("rename", () => {
  it("keeps the id and stores the definition unchanged", async () => {
    const query = savedQuery();
    await openPane(query);
    vi.stubGlobal("prompt", vi.fn(() => "Renamed query"));

    fireEvent.click(button(/rename/i));

    await waitFor(() => expect(store.put).toHaveBeenCalledTimes(1));
    const written = store.put.mock.calls[0][0] as SavedObject;
    expect(written.id).toBe("q-1"); // the SAME id — a rename is not a new object
    expect(written.name).toBe("Renamed query");
    expect(written.version).toBe(3); // optimistic-concurrency echo
    expect(written.definition).toEqual(query.definition); // definition untouched
  });

  it("does not write a drifted definition back repaired or rewritten", async () => {
    // The persistence path is where the "never rewrites the definition" invariant
    // can actually break: a rename of a query whose column was dropped must store
    // exactly the bytes it read, not a re-derived (self-healed) definition.
    const query = savedQuery();
    const before = JSON.stringify(query.definition);
    await openPane(query, withoutOrdersTotal());
    vi.stubGlobal("prompt", vi.fn(() => "Still broken"));

    fireEvent.click(button(/rename/i));

    await waitFor(() => expect(store.put).toHaveBeenCalledTimes(1));
    const written = store.put.mock.calls[0][0] as SavedObject;
    expect(JSON.stringify(written.definition)).toBe(before);
  });
});

describe("delete", () => {
  it("removes the saved query only when the confirmation is accepted", async () => {
    await openPane(savedQuery());
    vi.stubGlobal("confirm", vi.fn(() => true));

    fireEvent.click(button(/delete/i));

    await waitFor(() => expect(store.remove).toHaveBeenCalledWith("query", "q-1"));
  });

  it("never removes anything when the confirmation is declined", async () => {
    await openPane(savedQuery());
    vi.stubGlobal("confirm", vi.fn(() => false));

    fireEvent.click(button(/delete/i));

    await waitFor(() => expect(window.confirm).toHaveBeenCalled());
    expect(store.remove).not.toHaveBeenCalled();
  });
});

describe("degraded mode", () => {
  it("blocks every action that would run or store a drifted design", async () => {
    await openPane(savedQuery(), withoutOrdersTotal());

    expect((await screen.findByRole("alert")).textContent).toContain("dbo.orders.total");
    expect(button(/^save$/i).disabled).toBe(true);
    expect(button(/save as/i).disabled).toBe(true);
    expect(button(/view sql/i).disabled).toBe(true);
    expect(button(/^run$/i).disabled).toBe(true);
  });

  it("opens degraded even when the stored definition carries an empty fingerprint", async () => {
    // A definition from an older build whose persisted fingerprint under-reports
    // the design's references: the state still references the dropped column, so
    // the drift verdict must come from the state, not the stored side-copy.
    const stale = { ...serializeQuery(designedState()), fingerprint: { tables: [], columns: [] } };
    await openPane(savedQuery(stale), withoutOrdersTotal());

    expect((await screen.findByRole("alert")).textContent).toContain("dbo.orders.total");
    expect(button(/^save$/i).disabled).toBe(true);
  });
});

describe("open request", () => {
  it("is consumed one-shot so a remount cannot replay it", async () => {
    const { onOpenHandled } = await openPane(savedQuery());

    expect(onOpenHandled).toHaveBeenCalledTimes(1);
  });

  it("reports a definition it cannot open instead of crashing the pane", async () => {
    // A column with no criteria array: designer-state reads c.criteria inside a
    // render-time memo, and there is no ErrorBoundary — this must be reported.
    const malformed = {
      kind: "bifrost.visual-query",
      version: 1,
      state: {
        tables: [{ table: "dbo.orders", alias: null }],
        columns: [{ tableRef: "dbo.orders", column: "total", show: true, sort: "none" }],
        joins: [],
      },
    };
    bridge.getBuilderSchema.mockResolvedValue(schema());
    render(<QueryBuilderPane openRequest={savedQuery(malformed)} />);

    expect((await screen.findByRole("alert")).textContent).toContain(
      "is not a saved visual query this version can open",
    );
  });

  it("confirms before discarding unsaved edits, and keeps them when declined", async () => {
    bridge.getBuilderSchema.mockResolvedValue(schema());
    const { rerender } = render(<QueryBuilderPane />);
    // Unsaved work on the canvas: add a table from the palette.
    fireEvent.click(await screen.findByRole("button", { name: "Add dbo.customers" }));
    await screen.findByLabelText(/unsaved changes/i);

    vi.stubGlobal("confirm", vi.fn(() => false));
    rerender(<QueryBuilderPane openRequest={savedQuery()} />);

    await waitFor(() => expect(window.confirm).toHaveBeenCalled());
    // Declined: the canvas keeps the unsaved design, and no query is now open.
    expect(screen.getByText("Untitled query")).toBeTruthy();
  });
});
