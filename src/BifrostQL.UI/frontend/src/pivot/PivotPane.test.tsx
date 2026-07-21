// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { PivotPane } from "./PivotPane";
import type { PivotDefinition } from "./pivot-model";

afterEach(cleanup);
const definition: PivotDefinition = { kind: "bifrost.pivot", version: 1, source: { kind: "table", table: "sales" }, rowKeys: ["region"], pivotColumn: "quarter", valueColumn: "amount", aggregate: "sum" };
const schema = { _dbSchema: [{ graphQlName: "sales", columns: [{ graphQlName: "region" }, { graphQlName: "quarter" }, { graphQlName: "amount" }] }] };
const payload = { salesPivot: { pivotColumn: "quarter", rowKeys: ["region"], columns: ["Q1", "Q2", "(null)", ""], rows: [{ region: "north", cells: { Q1: 12, Q2: 7, "(null)": 1, "": 2 } }, { region: "south", cells: { Q1: 3, Q2: 9, "(null)": 0, "": 0 } }] } };
function fetcher(response = payload) { return { query: vi.fn((query: string) => Promise.resolve(query.includes("PivotSchema") ? schema : response)) }; }

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
    fireEvent.change(screen.getByLabelText("Pivot rows"), { target: { value: "amount" } });
    await vi.advanceTimersByTimeAsync(300);
    expect(live.query).toHaveBeenCalledTimes(3);
    vi.useRealTimers();
  });

  it("keeps the grid visible and gives cardinality errors actionable guidance", async () => {
    const live = { query: vi.fn((query: string) => query.includes("PivotSchema") ? Promise.resolve(schema) : Promise.reject(new Error("Pivot column 'quarter' has 101 distinct values in scope"))) };
    render(<PivotPane fetcher={live as never} initialDefinition={definition} store={{ list: vi.fn().mockResolvedValue([]) } as never} />);
    await screen.findByRole("alert");
    expect(screen.getByRole("alert").textContent).toContain("quarter");
    expect(screen.getByRole("alert").textContent).toContain("filter");
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
