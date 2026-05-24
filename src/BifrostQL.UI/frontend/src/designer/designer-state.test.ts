import { describe, expect, it } from "vitest";
import {
  emptyDesignerState,
  addTable,
  removeTable,
  toggleColumnShow,
  isColumnShown,
  toSpec,
  type DesignerState,
} from "./designer-state";

describe("designer-state", () => {
  it("adds a table referenced by its qualified name (no alias for the first instance)", () => {
    const s = addTable(emptyDesignerState, "dbo.users");
    expect(s.tables).toEqual([{ table: "dbo.users", alias: null }]);
  });

  it("assigns a unique alias to a duplicate table (self-join)", () => {
    let s = addTable(emptyDesignerState, "dbo.users");
    s = addTable(s, "dbo.users");
    expect(s.tables[0].alias).toBeNull();
    expect(s.tables[1].alias).toBe("users_2");

    // A third instance gets a further-distinct alias.
    s = addTable(s, "dbo.users");
    expect(s.tables[2].alias).toBe("users_3");
  });

  it("toggles a column on then off", () => {
    let s = addTable(emptyDesignerState, "dbo.users");
    s = toggleColumnShow(s, "dbo.users", "id");
    expect(isColumnShown(s, "dbo.users", "id")).toBe(true);
    expect(s.columns).toHaveLength(1);

    s = toggleColumnShow(s, "dbo.users", "id");
    expect(isColumnShown(s, "dbo.users", "id")).toBe(false);
    // Unsorted column is dropped entirely when unchecked.
    expect(s.columns).toHaveLength(0);
  });

  it("removeTable drops the table, its columns, and joins touching it", () => {
    let s = addTable(emptyDesignerState, "dbo.users");
    s = addTable(s, "dbo.orders");
    s = toggleColumnShow(s, "dbo.users", "id");
    s = toggleColumnShow(s, "dbo.orders", "total");
    const withJoin: DesignerState = {
      ...s,
      joins: [
        { leftTable: "dbo.orders", leftColumns: ["user_id"], rightTable: "dbo.users", rightColumns: ["id"], type: "inner" },
      ],
    };

    const after = removeTable(withJoin, "dbo.users");
    expect(after.tables.map((t) => t.table)).toEqual(["dbo.orders"]);
    expect(after.columns.every((c) => c.tableRef !== "dbo.users")).toBe(true);
    expect(after.joins).toHaveLength(0);
  });

  it("toSpec projects tables and shown columns into a VisualQuerySpec", () => {
    let s = addTable(emptyDesignerState, "dbo.users");
    s = toggleColumnShow(s, "dbo.users", "id");
    s = toggleColumnShow(s, "dbo.users", "name");

    const spec = toSpec(s);
    expect(spec.tables).toEqual([{ table: "dbo.users", alias: null }]);
    expect(spec.columns.map((c) => c.column)).toEqual(["id", "name"]);
    expect(spec.columns.every((c) => c.show)).toBe(true);
    expect(spec.joins).toEqual([]);
    expect(spec.filter).toBeNull();
  });
});
