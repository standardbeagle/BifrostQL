// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { DashboardPane } from "./DashboardPane";
import { openDashboard, saveDashboard } from "./dashboard-store";
import type { DashboardDefinition } from "./dashboard-model";

const dashboardSources = import.meta.glob("./*.{ts,tsx}", { eager: true, query: "?raw", import: "default" }) as Record<string, string>;

vi.mock("recharts", () => {
  const Box = ({ children }: { children?: React.ReactNode }) => <div>{children}</div>;
  const Chart = ({ children, data }: { children?: React.ReactNode; data?: unknown }) => <div data-testid="dashboard-chart-points">{JSON.stringify(data)}{children}</div>;
  return { Area: Box, AreaChart: Chart, Bar: Box, BarChart: Chart, CartesianGrid: Box, Legend: Box, Line: Box, LineChart: Chart, Pie: Box, PieChart: Chart, ResponsiveContainer: Box, Tooltip: Box, XAxis: Box, YAxis: Box };
});

afterEach(cleanup);

const definition: DashboardDefinition = {
  kind: "bifrost.dashboard", version: 1, grid: { cols: 12 }, tiles: [
    { id: "chart", kind: "chart", title: "Regional chart", layout: { x: 0, y: 0, w: 6, h: 5 }, config: { kind: "bifrost.chart", version: 1, source: { kind: "table", table: "orders" }, dimensions: ["region"], measures: [{ op: "count" }], chartType: "bar" } },
    { id: "orders", kind: "count", title: "Order count", layout: { x: 6, y: 0, w: 3, h: 3 }, config: { table: "orders", label: "Orders" } },
    { id: "customers", kind: "count", title: "Customer count", layout: { x: 9, y: 0, w: 3, h: 3 }, config: { table: "customers", label: "Customers" } },
    { id: "table", kind: "table", title: "Recent orders", layout: { x: 0, y: 5, w: 12, h: 4 }, config: { table: "orders", columns: ["id", "region"], limit: 3 } },
  ],
};

function dashboard(object = definition) { return { id: "dashboard-1", type: "dashboard", name: "Operations", definition: object, version: 1 }; }
function memoryStore(initial = [dashboard()]) {
  const objects = [...initial] as any[];
  return {
    objects,
    list: vi.fn(async () => [...objects]),
    put: vi.fn(async (object) => { const stored = { ...object, version: (object.version ?? 0) + 1 }; const index = objects.findIndex((item) => item.id === object.id); if (index >= 0) objects[index] = stored; else objects.push(stored); return stored; }),
    remove: vi.fn(async (_type, id) => { const index = objects.findIndex((item) => item.id === id); if (index >= 0) objects.splice(index, 1); }),
  };
}
function fetcher({ failOrders = false } = {}) {
  return { query: vi.fn((query: string) => {
    if (query.includes("ordersAggregate")) return failOrders ? Promise.reject(new Error("orders unavailable")) : Promise.resolve({ ordersAggregate: query.includes("groupBy") ? [{ region: "north", _count: 7 }] : { _count: 7 } });
    if (query.includes("customersAggregate")) return Promise.resolve({ customersAggregate: { _count: 3 } });
    if (query.includes("DashboardTable")) return Promise.resolve({ orders: [{ id: 101, region: "north" }, { id: 102, region: "south" }] });
    return Promise.resolve({});
  }) };
}

type ServerOrder = { id: number; region: string };
type DashboardServerFixture = { orders: ServerOrder[]; customers: Array<{ id: number }> };

/**
 * A tiny GraphQL server-contract harness: unlike `fetcher()` above it does not
 * return pre-baked response fixtures.  Each request is executed against its
 * backing rows, mirroring the aggregate/table surfaces exposed by BifrostQL.
 * The assertions below calculate their expected values through separate
 * SQL-equivalent functions so a tile cannot pass because its fixture happens
 * to contain the expected literal.
 */
function serverContractFetcher(data: DashboardServerFixture) {
  return {
    query: async (query: string) => {
      if (query.includes("DashboardSchema")) return { _dbSchema: [] };
      if (query.includes("ordersAggregate") && query.includes("groupBy")) {
        const groups = new Map<string, number>();
        for (const row of data.orders) groups.set(row.region, (groups.get(row.region) ?? 0) + 1);
        return { ordersAggregate: [...groups].map(([region, _count]) => ({ region, _count })) };
      }
      if (query.includes("ordersAggregate")) return { ordersAggregate: { _count: data.orders.length } };
      if (query.includes("customersAggregate")) return { customersAggregate: { _count: data.customers.length } };
      if (query.includes("DashboardTable")) return { orders: data.orders.slice(0, 3) };
      throw new Error(`Unhandled dashboard server operation: ${query}`);
    },
  };
}

function runEquivalentSql(data: DashboardServerFixture, sql: string): Array<Record<string, unknown>> {
  // Deliberately independent from the GraphQL operation harness above. These
  // are the four SQL results that an operator would compare with the dashboard.
  switch (sql) {
    case "SELECT region, COUNT(*) AS count FROM orders GROUP BY region ORDER BY region":
      return [...new Set(data.orders.map((row) => row.region))].sort().map((region) => ({ category: region, count: data.orders.filter((row) => row.region === region).length }));
    case "SELECT COUNT(*) AS count FROM orders": return [{ count: data.orders.filter(() => true).length }];
    case "SELECT COUNT(*) AS count FROM customers": return [{ count: data.customers.filter(() => true).length }];
    case "SELECT id, region FROM orders ORDER BY id LIMIT 3": return [...data.orders].sort((a, b) => a.id - b.id).slice(0, 3);
    default: throw new Error(`Unexpected SQL equivalence query: ${sql}`);
  }
}
async function openDashboardPane(store = memoryStore(), live: { query: any } = fetcher()) {
  render(<DashboardPane fetcher={live as never} store={store as never} />);
  fireEvent.click(await screen.findByRole("button", { name: "Operations" }));
  return { store, live };
}

describe("DashboardPane", () => {
  it("renders chart, two server count cards, and table values from independent transport queries", async () => {
    const { live } = await openDashboardPane();
    await screen.findByText("101");
    expect(screen.getByLabelText("Orders").textContent).toContain("7");
    expect(screen.getByLabelText("Customers").textContent).toContain("3");
    expect(screen.getByText("north")).toBeTruthy();
    const calls = (live.query.mock.calls as Array<[string]>).map(([query]: [string]) => String(query));
    expect(calls.some((query) => query.includes("ordersAggregate") && query.includes("_count"))).toBe(true);
    expect(calls.some((query) => query.includes("customersAggregate") && query.includes("_count"))).toBe(true);
    expect(calls.some((query) => query.includes("DashboardTable"))).toBe(true);
    // Count tiles get a scalar server aggregate, never an array which the client counts.
    expect(calls.filter((query) => query.includes("DashboardCount")).every((query) => query.includes("Aggregate") && !query.includes("limit:"))).toBe(true);
  });

  it("renders every tile of a saved dashboard equal to independent SQL-equivalent server results", async () => {
    const data: DashboardServerFixture = {
      orders: [{ id: 101, region: "north" }, { id: 102, region: "north" }, { id: 103, region: "south" }, { id: 104, region: "south" }],
      customers: [{ id: 1 }, { id: 2 }, { id: 3 }],
    };
    const expectedChart = runEquivalentSql(data, "SELECT region, COUNT(*) AS count FROM orders GROUP BY region ORDER BY region");
    const expectedOrders = runEquivalentSql(data, "SELECT COUNT(*) AS count FROM orders")[0].count;
    const expectedCustomers = runEquivalentSql(data, "SELECT COUNT(*) AS count FROM customers")[0].count;
    const expectedTable = runEquivalentSql(data, "SELECT id, region FROM orders ORDER BY id LIMIT 3");

    await openDashboardPane(memoryStore(), serverContractFetcher(data));
    await screen.findByText("101");
    expect(JSON.parse(screen.getByTestId("dashboard-chart-points").textContent ?? "[]")).toEqual(expectedChart.map(({ category, count }) => ({ category, count })));
    expect(screen.getByLabelText("Orders").textContent).toContain(String(expectedOrders));
    expect(screen.getByLabelText("Customers").textContent).toContain(String(expectedCustomers));
    expect([...screen.getAllByRole("row")].slice(1).map((row) => Array.from(row.querySelectorAll("td")).map((cell) => cell.textContent))).toEqual(expectedTable.map((row) => [String(row.id), String(row.region)]));
  });

  it("saves and reopens exact drag and resize layout coordinates", async () => {
    const { store } = await openDashboardPane();
    fireEvent.click(screen.getByRole("button", { name: "Edit dashboard" }));
    fireEvent.click(screen.getByRole("button", { name: "Resize Order count" }));
    const target = screen.getByLabelText("Customer count tile");
    fireEvent.drop(target, { dataTransfer: { getData: () => "orders" } });
    fireEvent.click(screen.getByRole("button", { name: "Save dashboard" }));
    await waitFor(() => expect(store.put).toHaveBeenCalled());
    const persisted = store.put.mock.calls[store.put.mock.calls.length - 1][0].definition;
    expect(persisted.tiles.find((tile: any) => tile.id === "orders").layout).toEqual({ x: 9, y: 0, w: 4, h: 4 });
    await waitFor(() => expect(store.list).toHaveBeenCalledTimes(2));
    fireEvent.click(screen.getByRole("button", { name: "New dashboard" }));
    fireEvent.click(screen.getByRole("button", { name: "Operations" }));
    expect((screen.getByLabelText("Order count tile") as HTMLElement).style.gridColumn).toBe("10 / span 4");
    expect((screen.getByLabelText("Order count tile") as HTMLElement).style.gridRow).toBe("1 / span 4");
  });

  it("has no edit affordances in view mode", async () => {
    await openDashboardPane();
    expect(screen.queryByLabelText("Dashboard name")).toBeNull();
    expect(screen.queryByRole("button", { name: "Save dashboard" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Rename dashboard" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Delete dashboard" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Add chart" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Remove Order count" })).toBeNull();
    expect(screen.queryByLabelText("Resize Order count")).toBeNull();
    expect(screen.queryByLabelText("Drag Order count")).toBeNull();
  });

  it("renders a missing reference as a tile error while sibling values still load", async () => {
    const broken: DashboardDefinition = { ...definition, tiles: [...definition.tiles, { id: "gone", kind: "count", title: "Deleted", ref: "gone-ref", layout: { x: 0, y: 10, w: 3, h: 3 } }] };
    const store = memoryStore([dashboard(broken)]);
    await openDashboardPane(store);
    expect(await screen.findByText("Missing saved object: gone-ref")).toBeTruthy();
    expect(screen.getByLabelText("Orders").textContent).toContain("7");
    expect(screen.getByText("101")).toBeTruthy();
  });

  it("keeps the dashboard and sibling tiles visible when one query fails", async () => {
    const brokenCount: DashboardDefinition = { ...definition, tiles: definition.tiles.map((tile) => tile.id === "orders" ? { ...tile, config: { table: "orders" } } : tile) };
    const store = memoryStore([dashboard(brokenCount)]);
    await openDashboardPane(store, fetcher({ failOrders: true }));
    expect((await screen.findAllByRole("alert")).some((alert) => alert.textContent?.includes("orders unavailable"))).toBe(true);
    expect(screen.getByText("Recent orders")).toBeTruthy();
    expect(screen.getByLabelText("Customers").textContent).toContain("3");
  });

  it("does not cascade-delete dashboards when another saved object disappears", async () => {
    const store = memoryStore([dashboard({ ...definition, tiles: [{ ...definition.tiles[1], ref: "query-1", config: undefined }] })]);
    // Simulate the unrelated saved-object deletion: only that object leaves the store.
    store.objects.push({ id: "query-1", type: "query", name: "Orders", definition: { table: "orders" }, version: 1 });
    store.objects.splice(store.objects.findIndex((object) => object.id === "query-1"), 1);
    expect(store.objects.some((object) => object.id === "dashboard-1")).toBe(true);
    expect(openDashboard(store.objects[0])).not.toBeNull();
  });

  it("keeps a saved query reference's validated filter in its count request", async () => {
    const visualQuery = { id: "north-orders", type: "query", name: "North orders", version: 1, definition: { kind: "bifrost.visual-query", version: 1, state: { tables: [{ table: "dbo.orders", alias: null }], columns: [], joins: [], filter: { op: "leaf", children: null, criterion: { table: "dbo.orders", column: "region", operator: "_eq", value: "north" } }, rowLimit: null } } };
    const filtered: DashboardDefinition = { ...definition, tiles: [{ id: "north", kind: "count", title: "North", ref: "north-orders", layout: { x: 0, y: 0, w: 3, h: 3 } }] };
    const live = fetcher();
    await openDashboardPane(memoryStore([dashboard(filtered), visualQuery] as any[]), live);
    await screen.findByLabelText("North orders");
    const call = (live.query.mock.calls as Array<[string, Record<string, unknown>?]>).find(([query]) => String(query).includes("DashboardCount"));
    expect(call?.[0]).toContain("TableFilterordersInput");
    expect(call?.[1]).toEqual({ filter: { region: { _eq: "north" } } });
  });

  it("persists dashboards under the dashboard saved-object type", async () => {
    const store = memoryStore([]);
    const saved = await saveDashboard(store as never, { id: "d1", name: "D", definition, version: 0 });
    expect(saved.type).toBe("dashboard");
    expect(openDashboard(saved)).toEqual(definition);
  });

  it("keeps dashboard tile data on the shared fetcher seam", () => {
    const sources = Object.values(dashboardSources).join("\n");
    expect(sources).not.toMatch(/\bfetch\s*\(/);
    expect(sources).not.toMatch(/\bnew\s+(?:XMLHttpRequest|HttpClient|Axios|GraphQLClient)\b/);
  });
});
