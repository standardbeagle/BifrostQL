import { describe, expect, it } from "vitest";
import { buildSchema, parse, validate } from "graphql";
import { buildChartAggregateQuery, mapAggregateRows, MAX_CHART_CATEGORIES, NULL_CATEGORY_LABEL, type ChartDefinition } from "./chart-model";
import { openChart, saveChart } from "./chart-store";

const definition = (chartType: ChartDefinition["chartType"] = "bar"): ChartDefinition => ({
  kind: "bifrost.chart", version: 1, chartType, source: { kind: "table", table: "orders", filterType: "ordersFilter", filter: { status: { _eq: "paid" } } },
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
      type Query { ordersAggregate(filter: ordersFilter, groupBy: [ordersColumn!]): [ordersAggregate!]! }
      input ordersFilter { status: StringFilter }
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

  it("saves charts as query saved objects and reloads an identical definition", async () => {
    const put = async (value: any) => ({ ...value, version: 1 });
    const stored = await saveChart({ put } as any, { id: "chart-1", name: "Revenue", definition: definition(), version: 0 });
    expect(stored.type).toBe("query");
    expect(openChart(stored)).toEqual(definition());
  });
});
