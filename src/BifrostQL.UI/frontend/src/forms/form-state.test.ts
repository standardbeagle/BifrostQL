import { describe, expect, it } from "vitest";
import {
  inferControlType,
  humanizeLabel,
  buildFormFromTable,
  toggleField,
  setFieldLabel,
  setFieldControl,
  setFieldReadOnly,
  setTitle,
  setLayoutColumns,
  moveField,
  visibleFields,
} from "./form-state";
import type { BuilderColumn, BuilderSchema } from "../lib/builder-bridge";

function col(name: string, type: string, extra: Partial<BuilderColumn> = {}): BuilderColumn {
  return { table: "dbo.users", name, type, nullable: true, isPrimaryKey: false, ...extra };
}

const schema: BuilderSchema = {
  tables: [{ schema: "dbo", name: "users", qualified: "dbo.users" }],
  columns: [
    col("id", "int", { isPrimaryKey: true, nullable: false }),
    col("full_name", "varchar(100)", { nullable: false }),
    col("bio", "text"),
    col("is_active", "bit"),
    col("born_on", "date"),
    col("created_at", "datetime"),
    col("balance", "decimal(10,2)"),
  ],
  relationships: [],
};

describe("inferControlType", () => {
  it("maps SQL types to controls", () => {
    expect(inferControlType(col("x", "bit"))).toBe("checkbox");
    expect(inferControlType(col("x", "BOOLEAN"))).toBe("checkbox");
    expect(inferControlType(col("x", "date"))).toBe("date");
    expect(inferControlType(col("x", "datetime"))).toBe("datetime");
    expect(inferControlType(col("x", "timestamp"))).toBe("datetime");
    expect(inferControlType(col("x", "int"))).toBe("number");
    expect(inferControlType(col("x", "decimal(10,2)"))).toBe("number");
    expect(inferControlType(col("x", "text"))).toBe("textarea");
    expect(inferControlType(col("x", "varchar(50)"))).toBe("text");
    expect(inferControlType(col("x", "uniqueidentifier"))).toBe("text");
  });
});

describe("humanizeLabel", () => {
  it("turns snake_case and camelCase into Title Case", () => {
    expect(humanizeLabel("customer_id")).toBe("Customer Id");
    expect(humanizeLabel("customerId")).toBe("Customer Id");
    expect(humanizeLabel("is_active")).toBe("Is Active");
    expect(humanizeLabel("users")).toBe("Users");
  });
});

describe("buildFormFromTable", () => {
  it("creates one included field per column in schema order", () => {
    const def = buildFormFromTable(schema, "dbo.users");
    expect(def.table).toBe("dbo.users");
    expect(def.title).toBe("Users");
    expect(def.columns).toBe(1);
    expect(def.fields.map((f) => f.column)).toEqual([
      "id", "full_name", "bio", "is_active", "born_on", "created_at", "balance",
    ]);
    expect(def.fields.every((f) => f.include)).toBe(true);
  });

  it("makes the primary key read-only and NOT NULL columns required", () => {
    const def = buildFormFromTable(schema, "dbo.users");
    const id = def.fields.find((f) => f.column === "id")!;
    expect(id.readOnly).toBe(true);
    expect(id.required).toBe(false); // PK is not flagged required (it is read-only)

    const name = def.fields.find((f) => f.column === "full_name")!;
    expect(name.required).toBe(true);
    expect(name.control).toBe("text");

    expect(def.fields.find((f) => f.column === "bio")!.control).toBe("textarea");
    expect(def.fields.find((f) => f.column === "is_active")!.control).toBe("checkbox");
  });

  it("returns no fields for an unknown table", () => {
    expect(buildFormFromTable(schema, "dbo.missing").fields).toEqual([]);
  });
});

describe("transforms", () => {
  const base = buildFormFromTable(schema, "dbo.users");

  it("toggles a field in and out of the form", () => {
    const off = toggleField(base, "bio");
    expect(off.fields.find((f) => f.column === "bio")!.include).toBe(false);
    expect(visibleFields(off).some((f) => f.column === "bio")).toBe(false);
    const on = toggleField(off, "bio");
    expect(on.fields.find((f) => f.column === "bio")!.include).toBe(true);
  });

  it("edits label, control and read-only flags", () => {
    let d = setFieldLabel(base, "full_name", "Name");
    expect(d.fields.find((f) => f.column === "full_name")!.label).toBe("Name");
    d = setFieldControl(d, "bio", "text");
    expect(d.fields.find((f) => f.column === "bio")!.control).toBe("text");
    d = setFieldReadOnly(d, "balance", true);
    expect(d.fields.find((f) => f.column === "balance")!.readOnly).toBe(true);
  });

  it("sets the title", () => {
    expect(setTitle(base, "Customer").title).toBe("Customer");
  });

  it("clamps layout columns to 1–4", () => {
    expect(setLayoutColumns(base, 3).columns).toBe(3);
    expect(setLayoutColumns(base, 0).columns).toBe(1);
    expect(setLayoutColumns(base, 99).columns).toBe(4);
  });

  it("reorders fields and ignores out-of-range moves", () => {
    const moved = moveField(base, "full_name", -1);
    expect(moved.fields.map((f) => f.column).slice(0, 2)).toEqual(["full_name", "id"]);
    // first field can't move earlier; last can't move later — both no-ops
    expect(moveField(base, "id", -1)).toEqual(base);
    expect(moveField(base, "balance", 1)).toEqual(base);
    expect(moveField(base, "nope", 1)).toEqual(base);
  });

  it("does not mutate the input definition", () => {
    const snapshot = JSON.stringify(base);
    toggleField(base, "bio");
    setFieldLabel(base, "id", "X");
    moveField(base, "full_name", 1);
    expect(JSON.stringify(base)).toBe(snapshot);
  });
});
