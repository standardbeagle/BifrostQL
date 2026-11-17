// @vitest-environment jsdom
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup } from "@testing-library/react";
import { ChartPane } from "./ChartPane";
import type { ChartDefinition } from "./chart-model";

vi.mock("recharts", () => {
  const Box = ({ children }: { children?: React.ReactNode }) => <div>{children}</div>;
  const SankeyBox = ({ children }: { children?: React.ReactNode }) => <div data-testid="sankey">{children}</div>;
  // Bar surfaces its click contract: recharts invokes onClick with the entry
  // whose payload carries the mapped point ({ category, ...values }).
  const Bar = ({ onClick }: { onClick?: (entry: unknown) => void }) =>
    <button data-testid="bar" onClick={() => onClick?.({ payload: { category: "north" } })} />;
  return { Area: Box, AreaChart: Box, Bar, BarChart: Box, CartesianGrid: Box, Legend: Box, Line: Box, LineChart: Box, Pie: Box, PieChart: Box, ResponsiveContainer: Box, Sankey: SankeyBox, Tooltip: Box, XAxis: Box, YAxis: Box };
});

afterEach(cleanup);

const initial: ChartDefinition = {
  kind: "bifrost.chart", version: 1, source: { kind: "table", table: "orders" }, dimensions: ["region"], measures: [{ op: "count" }], chartType: "line", limit: 100,
};
const schema = { _dbSchema: [{ graphQlName: "orders", columns: [{ graphQlName: "region" }, { graphQlName: "amount" }] }] };

function fetcher() {
  return { query: vi.fn((query: string) => Promise.resolve(query.includes("ChartSchema") ? schema : { ordersAggregate: [{ region: "north", _count: 2 }] })) };
}

describe("ChartPane", () => {
  it("loads the declared _dbSchema shape and renders schema-derived picker choices", async () => {
    const liveFetcher = fetcher();
    render(<ChartPane fetcher={liveFetcher as never} initialDefinition={initial} store={{ list: vi.fn().mockResolvedValue([]) } as never} />);

    await screen.findByRole("option", { name: "orders" });
    expect(liveFetcher.query.mock.calls[0][0]).toBe("query ChartSchema { _dbSchema { graphQlName columns { graphQlName } } }");
    expect(screen.getByRole("option", { name: "region" })).toBeTruthy();
    expect(screen.getByRole("option", { name: "amount" })).toBeTruthy();
  });

  it("shows the flow-to picker only for sankey, groups by both dimensions, and renders the sankey", async () => {
    const liveFetcher = {
      query: vi.fn((query: string) => Promise.resolve(query.includes("ChartSchema")
        ? { _dbSchema: [{ graphQlName: "search_conversions", columns: [{ graphQlName: "searched_category" }, { graphQlName: "purchased_category" }] }] }
        : { search_conversionsAggregate: [{ searched_category: "Electronics", purchased_category: "Books", _count: 13 }] })),
    };
    render(<ChartPane fetcher={liveFetcher as never} store={{ list: vi.fn().mockResolvedValue([]) } as never} />);
    await screen.findByRole("option", { name: "search_conversions" });

    expect(screen.queryByLabelText("Sankey target dimension")).toBeNull();
    fireEvent.change(screen.getByLabelText("Chart type"), { target: { value: "sankey" } });
    fireEvent.change(screen.getByLabelText("Chart table"), { target: { value: "search_conversions" } });
    fireEvent.change(screen.getByLabelText("Chart dimension"), { target: { value: "searched_category" } });
    fireEvent.change(screen.getByLabelText("Sankey target dimension"), { target: { value: "purchased_category" } });

    await screen.findByTestId("sankey");
    const aggregate = liveFetcher.query.mock.calls.map((call) => call[0]).find((q: string) => q.includes("Aggregate"));
    expect(aggregate).toContain("groupBy: [searched_category, purchased_category]");
  });

  it("drills a clicked bar through onDrill as the dimension's grid filter", async () => {
    // The reverse of Visualize: a chart element click hands the shell the
    // table plus grid-shaped filters, so exploration and visualization
    // navigate BOTH ways.
    const onDrill = vi.fn();
    render(<ChartPane fetcher={fetcher() as never} initialDefinition={{ ...initial, chartType: "bar" }} onDrill={onDrill} store={{ list: vi.fn().mockResolvedValue([]) } as never} />);

    fireEvent.click(await screen.findByTestId("bar"));

    expect(onDrill).toHaveBeenCalledWith("orders", [{ column: "region", operator: "_eq", value: "north" }]);
  });

  it("saves and visibly reloads the exact chart definition through query saved objects", async () => {
    const saved: any[] = [];
    const store = {
      list: vi.fn().mockImplementation(async () => saved),
      put: vi.fn().mockImplementation(async (object) => { const result = { ...object, version: 1 }; saved.push(result); return result; }),
    };
    render(<ChartPane fetcher={fetcher() as never} initialDefinition={initial} store={store as never} />);

    fireEvent.change(screen.getByLabelText("Chart name"), { target: { value: "Regional revenue" } });
    fireEvent.click(screen.getByRole("button", { name: "Save chart" }));
    await waitFor(() => expect(store.put).toHaveBeenCalledTimes(1));
    expect(store.put.mock.calls[0][0]).toMatchObject({ type: "query", name: "Regional revenue", definition: initial });
    await screen.findByRole("option", { name: "Regional revenue" });

    fireEvent.change(screen.getByLabelText("Chart type"), { target: { value: "bar" } });
    expect((screen.getByLabelText("Chart type") as HTMLSelectElement).value).toBe("bar");
    fireEvent.change(screen.getByLabelText("Open saved chart"), { target: { value: saved[0].id } });
    expect((screen.getByLabelText("Chart type") as HTMLSelectElement).value).toBe("line");
    expect((screen.getByLabelText("Chart dimension") as HTMLSelectElement).value).toBe("region");
  });

  it("re-saves an opened chart in place instead of duplicating it", async () => {
    const saved: any[] = [{ id: "chart-1", type: "query", name: "Regional", version: 3, definition: initial }];
    const store = {
      list: vi.fn().mockImplementation(async () => saved),
      put: vi.fn().mockImplementation(async (object) => ({ ...object, version: (object.version ?? 0) + 1 })),
    };
    render(<ChartPane fetcher={fetcher() as never} store={store as never} />);
    await screen.findByRole("option", { name: "Regional" });
    fireEvent.change(screen.getByLabelText("Open saved chart"), { target: { value: "chart-1" } });
    fireEvent.change(screen.getByLabelText("Chart type"), { target: { value: "bar" } });
    fireEvent.click(screen.getByRole("button", { name: "Save chart" }));
    await waitFor(() => expect(store.put).toHaveBeenCalledTimes(1));
    expect(store.put.mock.calls[0][0]).toMatchObject({ id: "chart-1", version: 3 });
  });

  it("drops a Visualize-seeded filter when the table changes", async () => {
    const seeded: ChartDefinition = { ...initial, source: { kind: "table", table: "orders", filter: { region: { _eq: "north" } }, filterType: "TableFilterordersInput" } };
    const live = { query: vi.fn((query: string) => Promise.resolve(query.includes("ChartSchema")
      ? { _dbSchema: [...schema._dbSchema, { graphQlName: "customers", columns: [{ graphQlName: "city" }] }] }
      : { ordersAggregate: [], customersAggregate: [{ city: "Oslo", _count: 1 }] })) };
    render(<ChartPane fetcher={live as never} initialDefinition={seeded} store={{ list: vi.fn().mockResolvedValue([]) } as never} />);
    await screen.findByRole("option", { name: "customers" });
    fireEvent.change(screen.getByLabelText("Chart table"), { target: { value: "customers" } });
    fireEvent.change(screen.getByLabelText("Chart dimension"), { target: { value: "city" } });
    await waitFor(() => expect(live.query.mock.calls.some(([q]) => String(q).includes("customersAggregate"))).toBe(true));
    const call = (live.query.mock.calls as unknown as Array<[string, Record<string, unknown>?]>).find(([q]) => String(q).includes("customersAggregate"))!;
    expect(call[0]).not.toContain("TableFilterordersInput");
    expect(call[1]?.filter).toBeUndefined();
  });

  it("shows a schema load failure instead of empty pickers", async () => {
    const live = { query: vi.fn(() => Promise.reject(new Error("schema offline"))) };
    render(<ChartPane fetcher={live as never} store={{ list: vi.fn().mockResolvedValue([]) } as never} />);
    expect((await screen.findByRole("alert")).textContent).toContain("schema offline");
  });
});
