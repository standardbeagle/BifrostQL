import { describe, expect, it } from "vitest";
import { buildSchema, parse, validate } from "graphql";
import { buildChartAggregateQuery, mapAggregateRows, mapChartData, mapSankeyData, MAX_CHART_CATEGORIES, NULL_CATEGORY_LABEL, type ChartDefinition } from "./chart-model";
import { openChart, saveChart } from "./chart-store";

const definition = (chartType: ChartDefinition["chartType"] = "bar"): ChartDefinition => ({
  kind: "bifrost.chart", version: 1, chartType, source: { kind: "table", table: "orders", filterType: "TableFilterordersInput", filter: { status: { _eq: "paid" } } },
  dimensions: ["region"], measures: [{ op: "sum", column: "amount" }], limit: 100,
});
const groupedSqlFixture = [{ region: "north", _sum: { amount: 12 } }, { region: "south", _sum: { amount: 7 } }];

describe("chart aggregate model", () => {
  it.each(["bar", "line", "pie", "area"] as const)("maps server grouped values unchanged for %s", (chartType) => {
    // Equivalent SQL fixture: SELECT region, SUM(amount) FROM orders WHERE status='paid' GROUP BY region.
    expect(mapAggregateRows(groupedSqlFixture, definition(chartType)).map((p) => p.values["sum:amount"])).toEqual([12, 7]);
  });

  it("uses a server aggregate query and retains the active filter as a variable", () => {
    const result = buildChartAggregateQuery(definition());
    // TableSchemaGenerator declares aggregate(filter, groupBy) only.  Keeping
    // this contract assertion here catches unsupported pagination arguments.
    expect(result.query).toContain("ordersAggregate(filter: $filter, groupBy: [region]");
    expect(result.query).not.toMatch(/\blimit\s*:/);
    expect(result.query).not.toContain("paid");
    expect(result.variables).toEqual({ filter: { status: { _eq: "paid" } } });
  });

  it("validates its document against the server's grouped-aggregate argument contract", () => {
    // This mirrors TableSchemaGenerator: aggregate fields accept filter and
    // groupBy, whereas limit belongs only to normal paged table fields.
    const schema = buildSchema(`
      type Query { ordersAggregate(filter: TableFilterordersInput, groupBy: [ordersColumn!]): [ordersAggregate!]! }
      input TableFilterordersInput { status: StringFilter }
      input StringFilter { _eq: String }
      enum ordersColumn { region amount }
      type ordersAggregate { region: String, _sum: ordersAggregateFields, _count: Int! }
      type ordersAggregateFields { amount: Float }
    `);
    const { query } = buildChartAggregateQuery(definition());
    expect(validate(schema, parse(query)).map((error) => error.message)).toEqual([]);
  });

  it("does not accept user-supplied GraphQL identifiers", () => {
    const unsafe = definition(); unsafe.source.table = "orders) { evil";
    expect(() => buildChartAggregateQuery(unsafe)).toThrow("Invalid chart definition");
  });

  it("labels null categories explicitly and preserves empty-string categories", () => {
    const values = mapAggregateRows([{ region: null, _sum: { amount: 1 } }, { region: "", _sum: { amount: 2 } }], definition());
    expect(values.map((v) => v.category)).toEqual([NULL_CATEGORY_LABEL, ""]);
  });

  it("guards high-cardinality aggregates before rendering", () => {
    const rows = Array.from({ length: MAX_CHART_CATEGORIES + 1 }, (_, i) => ({ region: String(i), _sum: { amount: 1 } }));
    expect(() => mapAggregateRows(rows, definition())).toThrow("Too many categories");
  });

  const sankeyDefinition = (): ChartDefinition => ({
    kind: "bifrost.chart", version: 1, chartType: "sankey", source: { kind: "table", table: "search_conversions" },
    dimensions: ["searched_category", "purchased_category"], measures: [{ op: "count" }], limit: 100,
  });
  const flowFixture = [
    { searched_category: "Electronics", purchased_category: "Electronics", _count: 60 },
    { searched_category: "Electronics", purchased_category: "Books", _count: 13 },
    { searched_category: "Books", purchased_category: null, _count: 33 },
  ];

  it("groups a sankey by BOTH dimensions and selects both", () => {
    const { query } = buildChartAggregateQuery(sankeyDefinition());
    expect(query).toContain("groupBy: [searched_category, purchased_category]");
    expect(query).toContain("searched_category purchased_category _count");
  });

  it("rejects a sankey without two distinct dimensions", () => {
    const one = sankeyDefinition(); one.dimensions = ["searched_category"];
    expect(() => buildChartAggregateQuery(one)).toThrow("source and a target dimension");
    const same = sankeyDefinition(); same.dimensions = ["searched_category", "searched_category"];
    expect(() => buildChartAggregateQuery(same)).toThrow("two different sankey dimensions");
  });

  it("keeps a non-sankey chart single-dimension even when extra dimensions are stored", () => {
    // A saved sankey switched to a bar chart must degrade cleanly, not smuggle
    // the second dimension into groupBy.
    const bar = sankeyDefinition(); bar.chartType = "bar";
    expect(buildChartAggregateQuery(bar).query).toContain("groupBy: [searched_category]");
  });

  it("maps flows to indexed links with SEPARATE source and target nodes for a shared name", () => {
    const { nodes, links } = mapSankeyData(flowFixture, sankeyDefinition());
    // "Electronics" appears on BOTH sides: collapsing it into one node would
    // draw a cycle a sankey cannot lay out.
    expect(nodes.filter((n) => n.name === "Electronics")).toHaveLength(2);
    expect(nodes.map((n) => n.name)).toContain(NULL_CATEGORY_LABEL);
    expect(links).toHaveLength(3);
    for (const link of links) {
      expect(nodes[link.source]).toBeDefined();
      expect(nodes[link.target]).toBeDefined();
      expect(link.source).not.toBe(link.target);
    }
    expect(links[0].value).toBe(60);
  });

  it("drops null and non-positive flow values instead of rendering them", () => {
    const rows = [
      { searched_category: "A", purchased_category: "B", _count: 5 },
      { searched_category: "A", purchased_category: "C", _count: 0 },
      { searched_category: "A", purchased_category: "D", _count: null },
    ];
    expect(mapSankeyData(rows, sankeyDefinition()).links).toHaveLength(1);
  });

  it("guards high-cardinality flows before rendering", () => {
    const rows = Array.from({ length: MAX_CHART_CATEGORIES + 1 }, (_, i) => ({ searched_category: String(i), purchased_category: "x", _count: 1 }));
    expect(() => mapSankeyData(rows, sankeyDefinition())).toThrow("Too many flows");
  });

  it("routes each chart type to its mapped shape through mapChartData", () => {
    expect(mapChartData(flowFixture, sankeyDefinition()).kind).toBe("sankey");
    expect(mapChartData(groupedSqlFixture, definition()).kind).toBe("cartesian");
  });

  it("saves charts as query saved objects and reloads an identical definition", async () => {
    const put = async (value: any) => ({ ...value, version: 1 });
    const stored = await saveChart({ put } as any, { id: "chart-1", name: "Revenue", definition: definition(), version: 0 });
    expect(stored.type).toBe("query");
    expect(openChart(stored)).toEqual(definition());
  });
});
