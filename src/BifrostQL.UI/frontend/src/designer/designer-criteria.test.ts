import { describe, expect, it } from "vitest";
import {
  emptyDesignerState,
  addTable,
  toggleColumnShow,
  setColumnSort,
  setColumnCriterion,
  parseCriterionValue,
  toFilter,
  toSpec,
} from "./designer-state";

function base() {
  let s = addTable(emptyDesignerState, "dbo.users");
  s = addTable(s, "dbo.orders");
  return s;
}

describe("designer criteria grid", () => {
  it("a single criterion produces a single leaf filter", () => {
    let s = base();
    s = setColumnCriterion(s, "dbo.users", "id", 0, { operator: "_gt", value: 5 });

    expect(toFilter(s)).toEqual({
      op: "leaf",
      children: null,
      criterion: { table: "dbo.users", column: "id", operator: "_gt", value: 5 },
    });
  });

  it("criteria on two columns in the same OR-row are ANDed", () => {
    let s = base();
    s = setColumnCriterion(s, "dbo.users", "id", 0, { operator: "_gt", value: 5 });
    s = setColumnCriterion(s, "dbo.orders", "total", 0, { operator: "_lt", value: 100 });

    const f = toFilter(s)!;
    expect(f.op).toBe("and");
    expect(f.children).toHaveLength(2);
  });

  it("criteria in different OR-rows are ORed", () => {
    let s = base();
    s = setColumnCriterion(s, "dbo.users", "id", 0, { operator: "_eq", value: 1 });
    s = setColumnCriterion(s, "dbo.users", "id", 1, { operator: "_eq", value: 2 });

    const f = toFilter(s)!;
    expect(f.op).toBe("or");
    expect(f.children).toHaveLength(2);
    expect(f.children![0]).toMatchObject({ op: "leaf" });
  });

  it("no criteria yields a null filter", () => {
    expect(toFilter(base())).toBeNull();
  });

  it("setColumnSort assigns increasing sortOrder across columns", () => {
    let s = base();
    s = setColumnSort(s, "dbo.users", "id", "asc");
    s = setColumnSort(s, "dbo.orders", "total", "desc");

    const idCol = s.columns.find((c) => c.column === "id")!;
    const totalCol = s.columns.find((c) => c.column === "total")!;
    expect(idCol.sort).toBe("asc");
    expect(idCol.sortOrder).toBe(1);
    expect(totalCol.sortOrder).toBe(2);

    // Clearing resets the order.
    s = setColumnSort(s, "dbo.users", "id", "none");
    expect(s.columns.find((c) => c.column === "id")!.sortOrder).toBeNull();
  });

  it("toSpec embeds the derived filter and sort", () => {
    let s = base();
    s = toggleColumnShow(s, "dbo.users", "id");
    s = setColumnSort(s, "dbo.users", "id", "asc");
    s = setColumnCriterion(s, "dbo.users", "id", 0, { operator: "_gte", value: 10 });

    const spec = toSpec(s);
    expect(spec.filter).not.toBeNull();
    const idCol = spec.columns.find((c) => c.column === "id")!;
    expect(idCol.sort).toBe("asc");
    expect(idCol.sortOrder).toBe(1);
  });

  describe("parseCriterionValue", () => {
    it("coerces a numeric scalar", () => {
      expect(parseCriterionValue("_eq", "5")).toBe(5);
    });
    it("keeps a non-numeric scalar as string", () => {
      expect(parseCriterionValue("_eq", "abc")).toBe("abc");
    });
    it("splits _in into an array", () => {
      expect(parseCriterionValue("_in", "1, 2, 3")).toEqual([1, 2, 3]);
    });
    it("splits _between into two values", () => {
      expect(parseCriterionValue("_between", "10, 20")).toEqual([10, 20]);
    });
    it("_null is true (IS NULL)", () => {
      expect(parseCriterionValue("_null", "")).toBe(true);
    });
  });
});
