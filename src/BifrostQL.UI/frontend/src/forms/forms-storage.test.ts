import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { loadForms, upsertForm, deleteForm, type SavedForm } from "./forms-storage";
import type { FormDefinition } from "./form-state";

// In-memory localStorage shim — same approach as connection/session.test.ts
// (Vitest runs in the node env, so no DOM Storage is present).
function memStorage(): Storage {
  const m = new Map<string, string>();
  return {
    getItem: (k) => (m.has(k) ? m.get(k)! : null),
    setItem: (k, v) => void m.set(k, String(v)),
    removeItem: (k) => void m.delete(k),
    clear: () => m.clear(),
    key: (i) => [...m.keys()][i] ?? null,
    get length() { return m.size; },
  } as Storage;
}

const g = globalThis as unknown as { localStorage?: Storage };
beforeEach(() => { g.localStorage = memStorage(); });
afterEach(() => { delete g.localStorage; });

const def: FormDefinition = {
  table: "dbo.users",
  title: "Users",
  columns: 1,
  fields: [{ column: "id", label: "Id", control: "number", readOnly: true, required: false, include: true }],
};

describe("forms-storage", () => {
  it("returns an empty list when nothing is stored", () => {
    expect(loadForms()).toEqual([]);
  });

  it("upserts, persists and round-trips a form", () => {
    const after = upsertForm([], { id: "f1", name: "Customers", definition: def }, "2026-01-01T00:00:00Z");
    expect(after).toHaveLength(1);
    expect(after[0]).toMatchObject({ id: "f1", name: "Customers", updatedAt: "2026-01-01T00:00:00Z" });
    expect(loadForms()).toEqual(after);
  });

  it("replaces an existing form by id and moves it to the front", () => {
    let list = upsertForm([], { id: "a", name: "A", definition: def }, "2026-01-01T00:00:00Z");
    list = upsertForm(list, { id: "b", name: "B", definition: def }, "2026-01-02T00:00:00Z");
    list = upsertForm(list, { id: "a", name: "A2", definition: def }, "2026-01-03T00:00:00Z");
    expect(list.map((f) => f.id)).toEqual(["a", "b"]);
    expect(list[0].name).toBe("A2");
    expect(list[0].updatedAt).toBe("2026-01-03T00:00:00Z");
  });

  it("deletes a form by id", () => {
    let list = upsertForm([], { id: "a", name: "A", definition: def }, "t");
    list = upsertForm(list, { id: "b", name: "B", definition: def }, "t");
    list = deleteForm(list, "a");
    expect(list.map((f) => f.id)).toEqual(["b"]);
    expect(loadForms().map((f) => f.id)).toEqual(["b"]);
  });

  it("drops malformed entries on load", () => {
    localStorage.setItem(
      "bifrostql_saved_forms",
      JSON.stringify([{ id: "ok", name: "Ok", updatedAt: "t", definition: def }, { junk: true }, 42]),
    );
    const loaded = loadForms();
    expect(loaded).toHaveLength(1);
    expect((loaded[0] as SavedForm).id).toBe("ok");
  });

  it("tolerates non-JSON garbage", () => {
    localStorage.setItem("bifrostql_saved_forms", "{not json");
    expect(loadForms()).toEqual([]);
  });
});
