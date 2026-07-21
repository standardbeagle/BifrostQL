// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { buildPivotCsv, PivotPane } from "./PivotPane";
import type { PivotDefinition } from "./pivot-model";

afterEach(cleanup);
const definition: PivotDefinition = { kind: "bifrost.pivot", version: 1, source: { kind: "table", table: "sales" }, rowKeys: ["region"], pivotColumn: "quarter", valueColumn: "amount", aggregate: "sum" };
const schema = { _dbSchema: [{ graphQlName: "sales", columns: [{ graphQlName: "region" }, { graphQlName: "quarter" }, { graphQlName: "amount" }] }] };
const payload = { salesPivot: { pivotColumn: "quarter", rowKeys: ["region"], columns: ["Q1", "Q2", "(null)", ""], rows: [{ region: "north", cells: { Q1: 12, Q2: 7, "(null)": 1, "": 2 } }, { region: "south", cells: { Q1: 3, Q2: 9, "(null)": 0, "": 0 } }] } };
function fetcher(response = payload) { return { query: vi.fn((query: string) => Promise.resolve(query.includes("PivotSchema") ? schema : response)) }; }
const savedVisualQuery = { id: "north-sales", type: "query", name: "North sales", version: 1, definition: { kind: "bifrost.visual-query", version: 1, state: { tables: [{ table: "dbo.sales", alias: null }], columns: [], joins: [], filter: { op: "leaf", children: null, criterion: { table: "dbo.sales", column: "region", operator: "_eq", value: "north" } }, rowLimit: null } } };

describe("PivotPane", () => {
  it("renders every server cross-tab cell, including null and empty categories", async () => {
    const live = fetcher();
    render(<PivotPane fetcher={live as never} initialDefinition={definition} store={{ list: vi.fn().mockResolvedValue([]) } as never} />);
    await screen.findByText("12");
    expect(screen.getByText("7")).toBeTruthy(); expect(screen.getByText("3")).toBeTruthy(); expect(screen.getByText("9")).toBeTruthy();
    expect(screen.getByText("(null)")).toBeTruthy(); expect(screen.getByText("(empty string)")).toBeTruthy();
    const calls = live.query.mock.calls.map((call) => call[0]);
    expect(calls.some((query) => query.includes("salesPivot("))).toBe(true);
    expect(calls.every((query) => !query.includes("Aggregate"))).toBe(true);
  });

  it("debounces one server re-query when a field moves between wells", async () => {
    vi.useFakeTimers(); const live = fetcher();
    render(<PivotPane fetcher={live as never} initialDefinition={definition} store={{ list: vi.fn().mockResolvedValue([]) } as never} />);
    await vi.advanceTimersByTimeAsync(300);
    expect(live.query).toHaveBeenCalledTimes(2);
    fireEvent.change(screen.getByLabelText("Add pivot row"), { target: { value: "amount" } });
    await vi.advanceTimersByTimeAsync(300);
    expect(live.query).toHaveBeenCalledTimes(3);
    vi.useRealTimers();
  });

  it("keeps the last successful grid visible and gives later cardinality errors actionable guidance", async () => {
    let pivots = 0;
    const live = { query: vi.fn((query: string) => query.includes("PivotSchema") ? Promise.resolve(schema) : ++pivots === 1 ? Promise.resolve(payload) : Promise.reject(new Error("Pivot column 'quarter' has 101 distinct values in scope"))) };
    render(<PivotPane fetcher={live as never} initialDefinition={definition} store={{ list: vi.fn().mockResolvedValue([]) } as never} />);
    await screen.findByText("12");
    fireEvent.change(screen.getByLabelText("Pivot aggregate"), { target: { value: "count" } });
    await screen.findByRole("alert");
    expect(screen.getByRole("alert").textContent).toContain("quarter");
    expect(screen.getByRole("alert").textContent).toContain("filter");
    expect(screen.getByText("12")).toBeTruthy();
  });

  it("resolves a saved query to the equivalent backing-table filter pivot", async () => {
    const direct = fetcher();
    const filteredDefinition: PivotDefinition = { ...definition, source: { kind: "table", table: "sales", filterType: "TableFiltersalesInput", filter: { region: { _eq: "north" } } } };
    render(<PivotPane fetcher={direct as never} initialDefinition={filteredDefinition} store={{ list: vi.fn().mockResolvedValue([]) } as never} />);
    await screen.findByText("12");
    const directCall = direct.query.mock.calls.find((call) => String(call[0]).includes("salesPivot("));
    cleanup();
    const saved = fetcher();
    const savedDefinition: PivotDefinition = { ...definition, source: { kind: "saved-query", table: "", savedQueryRef: "north-sales" } };
    render(<PivotPane fetcher={saved as never} initialDefinition={savedDefinition} store={{ list: vi.fn().mockResolvedValue([savedVisualQuery]) } as never} />);
    await screen.findByText("12");
    const savedCall = (saved.query.mock.calls as unknown as Array<[string, Record<string, unknown>?]>).find((call) => String(call[0]).includes("salesPivot("));
    expect(savedCall?.[0]).toBe(directCall?.[0]);
    expect(savedCall?.[1]).toEqual({ filter: { region: { _eq: "north" } } });
  });

  it("sends multiple row keys in one debounced pivot request", async () => {
    vi.useFakeTimers(); const live = fetcher();
    render(<PivotPane fetcher={live as never} initialDefinition={definition} store={{ list: vi.fn().mockResolvedValue([]) } as never} />);
    await vi.advanceTimersByTimeAsync(300);
    fireEvent.change(screen.getByLabelText("Add pivot row"), { target: { value: "amount" } });
    fireEvent.change(screen.getByLabelText("Add pivot row"), { target: { value: "quarter" } });
    await vi.advanceTimersByTimeAsync(300);
    const pivotCalls = live.query.mock.calls.filter((call) => String(call[0]).includes("salesPivot("));
    expect(pivotCalls).toHaveLength(2);
    expect(pivotCalls[1][0]).toContain("rowKeys: [region, amount]");
    vi.useRealTimers();
  });

  it("exports the entire rendered matrix with the shared exporter", async () => {
    const csv = await buildPivotCsv(payload.salesPivot);
    expect(csv.split("\r\n")).toEqual(["region,Q1,Q2,(null),(empty string)", "north,12,7,1,2", "south,3,9,0,0"]);
  });

  it("saves and reopens an identical definition", async () => {
    const objects: any[] = []; const store = { list: vi.fn().mockResolvedValue(objects), put: vi.fn(async (item) => { const stored = { ...item, version: 1 }; objects.push(stored); return stored; }) };
    render(<PivotPane fetcher={fetcher() as never} initialDefinition={definition} store={store as never} />);
    fireEvent.change(screen.getByLabelText("Pivot name"), { target: { value: "Quarterly sales" } }); fireEvent.click(screen.getByRole("button", { name: "Save pivot" }));
    await waitFor(() => expect(store.put).toHaveBeenCalledTimes(1));
    expect(store.put.mock.calls[0][0].definition).toEqual(definition);
    await screen.findByRole("option", { name: "Quarterly sales" });
    fireEvent.change(screen.getByLabelText("Pivot aggregate"), { target: { value: "count" } });
    fireEvent.change(screen.getByLabelText("Open saved pivot"), { target: { value: objects[0].id } });
    expect((screen.getByLabelText("Pivot aggregate") as HTMLSelectElement).value).toBe("sum");
  });
});
