import { describe, expect, it } from "vitest";
import { resolveDesignerSource, resolveDesignerTable, type NamedTable } from "./visual-filter";
import type { VisualFilter } from "./visual-query";

/**
 * The designer holds DATABASE names; GraphQL needs GRAPHQL names. These differ
 * for sanitized names (`order items` → `order_items`), and for one bare name in
 * two schemas, where the deduped GraphQL names are `orders` / `sales_orders`.
 */
const tables: NamedTable[] = [
  { schema: "dbo", dbName: "orders", graphQlName: "orders", columns: [{ dbName: "region", graphQlName: "region" }] },
  { schema: "sales", dbName: "orders", graphQlName: "sales_orders", columns: [{ dbName: "region", graphQlName: "region" }] },
  { schema: "dbo", dbName: "order items", graphQlName: "order_items", columns: [{ dbName: "unit-price", graphQlName: "unit_price" }] },
];

const leaf = (table: string, column: string): VisualFilter => ({
  op: "leaf", children: null, criterion: { table, column, operator: "_gt", value: 5 },
});

describe("resolveDesignerTable", () => {
  it("maps a sanitized table and column onto their GraphQL names", () => {
    const names = resolveDesignerTable("dbo.order items", tables);
    expect(names.graphQlName).toBe("order_items");
    expect(names.column("unit-price")).toBe("unit_price");
  });

  it("resolves the second schema's same-named table to its own GraphQL name", () => {
    expect(resolveDesignerTable("sales.orders", tables).graphQlName).toBe("sales_orders");
    expect(resolveDesignerTable("dbo.orders", tables).graphQlName).toBe("orders");
  });

  it("refuses an ambiguous bare name instead of guessing a schema", () => {
    expect(() => resolveDesignerTable("orders", tables)).toThrow(/not present/);
  });

  it("resolves a unique bare name (schema-less providers)", () => {
    expect(resolveDesignerTable("order items", tables).graphQlName).toBe("order_items");
  });

  it("refuses an unknown column", () => {
    expect(() => resolveDesignerTable("dbo.orders", tables).column("missing")).toThrow(/missing/);
  });
});

describe("resolveDesignerSource", () => {
  it("keys the filter by GraphQL field names and types it by the GraphQL table", () => {
    const source = resolveDesignerSource({ table: "dbo.order items", alias: null }, leaf("dbo.order items", "unit-price"), tables);
    expect(source.table).toBe("order_items");
    expect(source.filter).toEqual({ unit_price: { _gt: 5 } });
    expect(source.filterType).toBe("TableFilterorder_itemsInput");
  });

  it("leaves filter and filterType unset without a filter", () => {
    const source = resolveDesignerSource({ table: "sales.orders", alias: null }, null, tables);
    expect(source).toMatchObject({ table: "sales_orders", filter: undefined, filterType: undefined });
  });
});
