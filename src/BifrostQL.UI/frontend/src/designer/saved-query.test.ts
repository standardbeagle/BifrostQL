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
  queryRefs,
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

/** A design whose only reference to `orders.total` lives in a nested filter tree. */
function filteredState(): DesignerState {
  let s = addTable(emptyDesignerState, "dbo.customers");
  s = addTable(s, "dbo.orders");
  s = toggleColumnShow(s, "dbo.customers", "name");
  return {
    ...s,
    filter: {
      op: "or",
      children: [
        { op: "leaf", children: null, criterion: { table: "dbo.orders", column: "total", operator: "_gt", value: 100 } },
      ],
      criterion: null,
    },
  };
}

describe("queryRefs", () => {
  it("captures every referenced table and column", () => {
    const refs = queryRefs(designedState());

    expect(refs.tables).toEqual(["dbo.customers", "dbo.orders"]);
    // Selected columns AND join columns are both live references.
    expect(refs.columns).toEqual([
      { table: "dbo.customers", column: "id" },
      { table: "dbo.customers", column: "name" },
      { table: "dbo.orders", column: "customer_id" },
      { table: "dbo.orders", column: "total" },
    ]);
  });

  it("captures a column referenced only from a nested filter tree", () => {
    const refs = queryRefs(filteredState());

    expect(refs.columns).toContainEqual({ table: "dbo.orders", column: "total" });
  });

  it("resolves self-join aliases back to their real table names", () => {
    let s = addTable(emptyDesignerState, "dbo.orders");
    s = addTable(s, "dbo.orders"); // second instance gets alias "orders_2"
    s = toggleColumnShow(s, "orders_2", "total");

    const refs = queryRefs(s);

    expect(refs.tables).toEqual(["dbo.orders"]);
    expect(refs.columns).toEqual([{ table: "dbo.orders", column: "total" }]);
    // The state keeps the alias — only the derived refs are de-aliased.
    expect(serializeQuery(s).state.columns[0].tableRef).toBe("orders_2");
  });
});

describe("serializeQuery", () => {
  it("stamps the kind and version and carries the state as designed", () => {
    const def = serializeQuery(designedState());

    expect(def.kind).toBe(SAVED_QUERY_KIND);
    expect(def.version).toBe(SAVED_QUERY_VERSION);
    expect(def.state).toEqual(designedState());
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
  /** A definition wrapper around an arbitrary (possibly malformed) state. */
  function def(state: unknown): unknown {
    return { kind: SAVED_QUERY_KIND, version: SAVED_QUERY_VERSION, state };
  }

  it("rejects values that are not a saved visual query", () => {
    expect(parseQueryDefinition(null)).toBeNull();
    expect(parseQueryDefinition({})).toBeNull();
    expect(parseQueryDefinition({ kind: "something-else", version: 1, state: {} })).toBeNull();
    expect(parseQueryDefinition({ kind: SAVED_QUERY_KIND, version: 99, state: {} })).toBeNull();
    // state present but structurally wrong
    expect(parseQueryDefinition(def({ tables: "nope", columns: [], joins: [] }))).toBeNull();
  });

  it("rejects a definition whose elements are malformed, not just its containers", () => {
    const tables = [{ table: "dbo.orders", alias: null }];

    // A null / non-object table.
    expect(parseQueryDefinition(def({ tables: [null], columns: [], joins: [] }))).toBeNull();
    expect(parseQueryDefinition(def({ tables: [{ alias: null }], columns: [], joins: [] }))).toBeNull();

    // A column with no criteria array: designer-state reads c.criteria.some/.length
    // inside a render-time memo, so this must never reach setState.
    expect(parseQueryDefinition(def({ tables, columns: [{}], joins: [] }))).toBeNull();
    expect(
      parseQueryDefinition(
        def({
          tables,
          columns: [{ tableRef: "dbo.orders", column: "total", show: true, sort: "none", sortOrder: null, alias: null }],
          joins: [],
        })
      )
    ).toBeNull();

    // A join missing its parallel column arrays.
    expect(
      parseQueryDefinition(
        def({ tables, columns: [], joins: [{ leftTable: "dbo.orders", rightTable: "dbo.customers", type: "inner" }] })
      )
    ).toBeNull();

    // A filter node that is neither a group with children nor a leaf with a criterion.
    expect(parseQueryDefinition(def({ tables, columns: [], joins: [], filter: { op: "leaf" } }))).toBeNull();
    expect(
      parseQueryDefinition(def({ tables, columns: [], joins: [], filter: { op: "and", children: [{ op: "leaf" }] } }))
    ).toBeNull();
  });

  it("accepts a well-formed definition, including one carrying a filter tree", () => {
    const parsed = parseQueryDefinition(serializeQuery(filteredState()) as unknown);

    expect(parsed).not.toBeNull();
    expect(parsed!.state.filter).not.toBeNull();
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

  it("reports a dropped column referenced only from the filter tree", () => {
    const live = schema();
    live.columns = live.columns.filter((c) => !(c.table === "dbo.orders" && c.name === "total"));

    const drift = detectSchemaDrift(serializeQuery(filteredState()), live);

    expect(hasDrift(drift)).toBe(true);
    expect(drift.missingColumns).toEqual([{ table: "dbo.orders", column: "total" }]);
  });

  it("derives drift from the state, so a definition cannot under-report it", () => {
    // A definition carrying a stale/emptied fingerprint from an older build: the
    // state still references dbo.orders.total, so the drift must still be found.
    const stale = { ...serializeQuery(designedState()), fingerprint: { tables: [], columns: [] } };
    const live = schema();
    live.columns = live.columns.filter((c) => !(c.table === "dbo.orders" && c.name === "total"));

    const drift = detectSchemaDrift(stale, live);

    expect(drift.missingColumns).toEqual([{ table: "dbo.orders", column: "total" }]);
  });
});
