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
  m2mJoinPlans,
  applyM2mJoinPlan,
  type DesignerState,
} from "./designer-state";

const schema = (
  relationships: BuilderSchema["relationships"],
  manyToMany: BuilderSchema["manyToMany"] = [],
): BuilderSchema => ({
  tables: [],
  columns: [],
  relationships,
  manyToMany,
});

// students <-> courses through enrollments, as the host emits it (one direction
// shown; the helper must also accept the reverse).
const studentsToCourses = {
  sourceTable: "dbo.students",
  sourceColumns: ["id"],
  junctionTable: "dbo.enrollments",
  junctionSourceColumns: ["student_id"],
  junctionTargetColumns: ["course_id"],
  targetTable: "dbo.courses",
  targetColumns: ["id"],
};
const coursesToStudents = {
  sourceTable: "dbo.courses",
  sourceColumns: ["id"],
  junctionTable: "dbo.enrollments",
  junctionSourceColumns: ["course_id"],
  junctionTargetColumns: ["student_id"],
  targetTable: "dbo.students",
  targetColumns: ["id"],
};

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

  it("m2mJoinPlans finds the through-junction path to an existing placed table", () => {
    // students placed; place courses; expect a plan bridging them via enrollments.
    let s = addTable(emptyDesignerState, "dbo.students");
    s = addTable(s, "dbo.courses");
    const plans = m2mJoinPlans(s, schema([], [studentsToCourses]), "dbo.courses");

    expect(plans).toHaveLength(1);
    const plan = plans[0];
    expect(plan.junctionTable).toBe("dbo.enrollments");
    expect(plan.joins).toHaveLength(2);
    // hop 1: existing (students) -> junction
    expect(plan.joins[0]).toMatchObject({
      leftTable: "dbo.students",
      leftColumns: ["id"],
      rightTable: "dbo.enrollments",
      rightColumns: ["student_id"],
      type: "inner",
    });
    // hop 2: junction -> new (courses)
    expect(plan.joins[1]).toMatchObject({
      leftTable: "dbo.enrollments",
      leftColumns: ["course_id"],
      rightTable: "dbo.courses",
      rightColumns: ["id"],
      type: "inner",
    });
  });

  it("m2mJoinPlans accepts the reverse direction and dedupes by junction", () => {
    let s = addTable(emptyDesignerState, "dbo.students");
    s = addTable(s, "dbo.courses");
    // Both directions present (as the host emits them) — still one plan.
    const plans = m2mJoinPlans(s, schema([], [studentsToCourses, coursesToStudents]), "dbo.courses");
    expect(plans).toHaveLength(1);
    expect(plans[0].junctionTable).toBe("dbo.enrollments");
  });

  it("m2mJoinPlans is empty when no m2m path connects the new table", () => {
    let s = addTable(emptyDesignerState, "dbo.users");
    s = addTable(s, "dbo.courses");
    expect(m2mJoinPlans(s, schema([], [studentsToCourses]), "dbo.courses")).toHaveLength(0);
  });

  it("applyM2mJoinPlan adds the junction table and both hops", () => {
    let s = addTable(emptyDesignerState, "dbo.students");
    s = addTable(s, "dbo.courses");
    const plan = m2mJoinPlans(s, schema([], [studentsToCourses]), "dbo.courses")[0];

    const result = applyM2mJoinPlan(s, plan);
    expect(result.tables.map((t) => t.table)).toContain("dbo.enrollments");
    expect(result.joins).toHaveLength(2);
  });
});
