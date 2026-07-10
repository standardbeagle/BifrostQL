import { afterEach, beforeEach, describe, expect, it } from "vitest";
import type { SavedObject, SavedObjectsClient } from "@standardbeagle/edit-db";
import { FORMS_MIGRATION_FLAG, migrateFormsToSavedObjects } from "./forms-migration";

const FORMS_KEY = "bifrostql_saved_forms";

/** In-memory localStorage shim — the frontend test env is node (no DOM storage). */
function memStorage(): Storage {
  const map = new Map<string, string>();
  return {
    get length() {
      return map.size;
    },
    clear: () => map.clear(),
    getItem: (k: string) => map.get(k) ?? null,
    key: (i: number) => Array.from(map.keys())[i] ?? null,
    removeItem: (k: string) => void map.delete(k),
    setItem: (k: string, v: string) => void map.set(k, v),
  } as Storage;
}

/** In-memory saved-objects client capturing puts. */
function fakeClient(seed: SavedObject[] = []): SavedObjectsClient & { puts: SavedObject[] } {
  const store = new Map(seed.map((o) => [`${o.type}/${o.id}`, o]));
  const puts: SavedObject[] = [];
  return {
    puts,
    async list() {
      return Array.from(store.values());
    },
    async get(type, id) {
      return store.get(`${type}/${id}`) ?? null;
    },
    async put(object) {
      const saved = { ...object, version: object.version + 1 };
      store.set(`${object.type}/${object.id}`, saved);
      puts.push(object);
      return saved;
    },
    async remove() {},
  };
}

function seedForm(id: string, name: string): void {
  const form = {
    id,
    name,
    updatedAt: "2026-01-01T00:00:00.000Z",
    definition: { table: "customers", title: name, columns: 1, fields: [] },
  };
  const existing = JSON.parse(localStorage.getItem(FORMS_KEY) ?? "[]") as unknown[];
  localStorage.setItem(FORMS_KEY, JSON.stringify([...existing, form]));
}

describe("migrateFormsToSavedObjects", () => {
  beforeEach(() => {
    (globalThis as { localStorage: Storage }).localStorage = memStorage();
  });
  afterEach(() => {
    delete (globalThis as { localStorage?: Storage }).localStorage;
  });

  it("imports local forms not present on the server and sets the done flag", async () => {
    seedForm("f1", "Customer");
    seedForm("f2", "Order");
    const client = fakeClient();

    const result = await migrateFormsToSavedObjects(client);

    expect(result).toEqual({ imported: 2, skipped: 0, alreadyDone: false });
    expect(client.puts.map((p) => p.id).sort()).toEqual(["f1", "f2"]);
    expect(client.puts.every((p) => p.type === "form" && p.version === 0)).toBe(true);
    expect(localStorage.getItem(FORMS_MIGRATION_FLAG)).toBe("true");
  });

  it("skips a form already on the server rather than overwriting it", async () => {
    seedForm("f1", "Customer");
    const server: SavedObject = { id: "f1", type: "form", name: "Server copy", definition: {}, version: 5 };
    const client = fakeClient([server]);

    const result = await migrateFormsToSavedObjects(client);

    expect(result).toEqual({ imported: 0, skipped: 1, alreadyDone: false });
    expect(client.puts).toHaveLength(0);
  });

  it("does nothing on a second run (flag already set)", async () => {
    seedForm("f1", "Customer");
    const client = fakeClient();
    await migrateFormsToSavedObjects(client);

    const second = await migrateFormsToSavedObjects(fakeClient());
    expect(second).toEqual({ imported: 0, skipped: 0, alreadyDone: true });
  });

  it("re-runs past the flag when forced", async () => {
    seedForm("f1", "Customer");
    localStorage.setItem(FORMS_MIGRATION_FLAG, "true");
    const client = fakeClient();

    const result = await migrateFormsToSavedObjects(client, { force: true });
    expect(result.imported).toBe(1);
  });
});
