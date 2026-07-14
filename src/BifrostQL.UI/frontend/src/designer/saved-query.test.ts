import { describe, it, expect } from "vitest";
import type { BuilderSchema } from "../lib/builder-bridge";
import {
  addTable,
  addJoin,
  toggleColumnShow,
  setColumnCriterion,
  emptyDesignerState,
  type DesignerState,
} from "./designer-state";
import {
  serializeQuery,
  parseQueryDefinition,
  detectSchemaDrift,
  hasDrift,
  SAVED_QUERY_KIND,
  SAVED_QUERY_VERSION,
} from "./saved-query";

/** Schema with two tables; `orders.customer_id` → `customers.id`. */
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

/** customers ⟕ orders, showing name + total, with a criterion on total. */
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
  s = setColumnCriterion(s, "dbo.orders", "total", 0, { operator: "_gt", value: 100 });
  return s;
}

describe("serializeQuery", () => {
  it("captures every referenced table and column in the schema fingerprint", () => {
    const def = serializeQuery(designedState());

    expect(def.kind).toBe(SAVED_QUERY_KIND);
    expect(def.version).toBe(SAVED_QUERY_VERSION);
    expect(def.fingerprint.tables).toEqual(["dbo.customers", "dbo.orders"]);
    // Selected columns AND join columns are both live references.
    expect(def.fingerprint.columns).toEqual([
      { table: "dbo.customers", column: "id" },
      { table: "dbo.customers", column: "name" },
      { table: "dbo.orders", column: "customer_id" },
      { table: "dbo.orders", column: "total" },
    ]);
  });

  it("resolves self-join aliases back to their real table names", () => {
    let s = addTable(emptyDesignerState, "dbo.orders");
    s = addTable(s, "dbo.orders"); // second instance gets alias "orders_2"
    s = toggleColumnShow(s, "orders_2", "total");

    const def = serializeQuery(s);

    expect(def.fingerprint.tables).toEqual(["dbo.orders"]);
    expect(def.fingerprint.columns).toEqual([{ table: "dbo.orders", column: "total" }]);
    // The state keeps the alias — only the fingerprint is de-aliased.
    expect(def.state.columns[0].tableRef).toBe("orders_2");
  });

  it("round-trips the designer state through JSON", () => {
    const state = designedState();
    const json = JSON.stringify(serializeQuery(state));

    const parsed = parseQueryDefinition(JSON.parse(json));

    expect(parsed).not.toBeNull();
    expect(parsed!.state).toEqual(state);
  });
});

describe("parseQueryDefinition", () => {
  it("rejects values that are not a saved visual query", () => {
    expect(parseQueryDefinition(null)).toBeNull();
    expect(parseQueryDefinition({})).toBeNull();
    expect(parseQueryDefinition({ kind: "something-else", version: 1, state: {}, fingerprint: {} })).toBeNull();
    expect(
      parseQueryDefinition({ kind: SAVED_QUERY_KIND, version: 99, state: {}, fingerprint: {} })
    ).toBeNull();
    // state present but structurally wrong
    expect(
      parseQueryDefinition({
        kind: SAVED_QUERY_KIND,
        version: SAVED_QUERY_VERSION,
        state: { tables: "nope", columns: [], joins: [] },
        fingerprint: { tables: [], columns: [] },
      })
    ).toBeNull();
  });
});

describe("detectSchemaDrift", () => {
  it("reports no drift when the live schema still has every reference", () => {
    const drift = detectSchemaDrift(serializeQuery(designedState()), schema());

    expect(hasDrift(drift)).toBe(false);
    expect(drift.missingTables).toEqual([]);
    expect(drift.missingColumns).toEqual([]);
  });

  it("reports a column dropped from the database as a broken reference", () => {
    const live = schema();
    live.columns = live.columns.filter((c) => !(c.table === "dbo.orders" && c.name === "total"));

    const drift = detectSchemaDrift(serializeQuery(designedState()), live);

    expect(hasDrift(drift)).toBe(true);
    expect(drift.missingTables).toEqual([]);
    expect(drift.missingColumns).toEqual([{ table: "dbo.orders", column: "total" }]);
  });

  it("reports a dropped table once, without also listing its columns", () => {
    const live = schema();
    live.tables = live.tables.filter((t) => t.qualified !== "dbo.orders");
    live.columns = live.columns.filter((c) => c.table !== "dbo.orders");

    const drift = detectSchemaDrift(serializeQuery(designedState()), live);

    expect(drift.missingTables).toEqual(["dbo.orders"]);
    expect(drift.missingColumns).toEqual([]);
  });

  it("never rewrites the definition it inspects", () => {
    const def = serializeQuery(designedState());
    const before = JSON.stringify(def);
    const live = schema();
    live.columns = live.columns.filter((c) => c.name !== "total");

    detectSchemaDrift(def, live);

    expect(JSON.stringify(def)).toBe(before);
  });
});
