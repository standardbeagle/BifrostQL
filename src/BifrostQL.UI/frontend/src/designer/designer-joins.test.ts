import { describe, expect, it } from "vitest";
import type { BuilderSchema } from "../lib/builder-bridge";
import {
  emptyDesignerState,
  addTable,
  addJoin,
  removeJoin,
  setJoinType,
  autoJoinCandidates,
  addTableWithAutoJoin,
  type DesignerState,
} from "./designer-state";

const schema = (relationships: BuilderSchema["relationships"]): BuilderSchema => ({
  tables: [],
  columns: [],
  relationships,
});

const ordersToUsers = {
  leftTable: "dbo.orders",
  leftColumns: ["user_id"],
  rightTable: "dbo.users",
  rightColumns: ["id"],
};
const usersToOrders = {
  leftTable: "dbo.users",
  leftColumns: ["last_order_id"],
  rightTable: "dbo.orders",
  rightColumns: ["id"],
};

function twoTables(): DesignerState {
  return addTable(addTable(emptyDesignerState, "dbo.users"), "dbo.orders");
}

describe("designer joins", () => {
  it("autoJoinCandidates finds the single FK path between placed tables", () => {
    const candidates = autoJoinCandidates(twoTables(), schema([ordersToUsers]), "dbo.orders");
    expect(candidates).toHaveLength(1);
    expect(candidates[0]).toMatchObject({
      leftTable: "dbo.orders",
      leftColumns: ["user_id"],
      rightTable: "dbo.users",
      rightColumns: ["id"],
      type: "inner",
    });
  });

  it("autoJoinCandidates returns empty when no relationship connects the tables", () => {
    expect(autoJoinCandidates(twoTables(), schema([]), "dbo.orders")).toHaveLength(0);
  });

  it("addTableWithAutoJoin wires a single FK path automatically", () => {
    const start = addTable(emptyDesignerState, "dbo.users");
    const { state, ambiguous } = addTableWithAutoJoin(start, schema([ordersToUsers]), "dbo.orders");
    expect(ambiguous).toHaveLength(0);
    expect(state.joins).toHaveLength(1);
    expect(state.joins[0].rightTable).toBe("dbo.users");
  });

  it("addTableWithAutoJoin surfaces ambiguity (bidirectional) without auto-applying", () => {
    const start = addTable(emptyDesignerState, "dbo.users");
    const { state, ambiguous } = addTableWithAutoJoin(start, schema([ordersToUsers, usersToOrders]), "dbo.orders");
    expect(ambiguous).toHaveLength(2);
    expect(state.joins).toHaveLength(0);
  });

  it("auto-join uses the alias ref for a duplicated (self-join) table", () => {
    // orders, then users, then users again (aliased users_2).
    let s = addTable(emptyDesignerState, "dbo.orders");
    s = addTable(s, "dbo.users");
    s = addTable(s, "dbo.users");
    const candidates = autoJoinCandidates(s, schema([ordersToUsers]), "users_2");
    // orders->users matches the second users instance; right side uses its alias.
    expect(candidates).toHaveLength(1);
    expect(candidates[0].leftTable).toBe("dbo.orders");
    expect(candidates[0].rightTable).toBe("users_2");
  });

  it("addJoin dedupes equivalent joins", () => {
    const s = addJoin(twoTables(), { ...ordersToUsers, type: "inner" });
    const again = addJoin(s, { ...ordersToUsers, type: "inner" });
    expect(again.joins).toHaveLength(1);
  });

  it("setJoinType and removeJoin edit the join list", () => {
    let s = addJoin(twoTables(), { ...ordersToUsers, type: "inner" });
    s = setJoinType(s, 0, "left");
    expect(s.joins[0].type).toBe("left");
    s = removeJoin(s, 0);
    expect(s.joins).toHaveLength(0);
  });
});
