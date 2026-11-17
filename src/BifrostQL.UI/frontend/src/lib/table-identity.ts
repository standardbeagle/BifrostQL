/**
 * Table identity across the editor → host seam. edit-db's `dbName` is the BARE
 * database name, and two schemas can share it (`sales.users`, `dbo.users`), so
 * anything that hands a table to a host resolving DB names (Copy DDL, the visual
 * query builder) sends `schema.dbName`.
 */

/** The subset of edit-db's Table this module reads. */
export interface EditorTableIdentity {
  schema?: string;
  dbName: string;
}

/** `schema.dbName` when the schema is known, else the bare `dbName`. */
export function qualifiedDbName(table: EditorTableIdentity): string {
  return table.schema ? `${table.schema}.${table.dbName}` : table.dbName;
}

/** The subset of a builder-schema table this module reads. */
export interface BuilderTableIdentity {
  name: string;
  qualified: string;
}

/**
 * Finds `wanted` among the builder's tables: an exact (case-insensitive)
 * qualified match first, else a bare name only when exactly one table carries
 * it. A bare name two schemas share resolves to nothing, never to whichever
 * table is listed first.
 */
export function findBuilderTable<T extends BuilderTableIdentity>(tables: readonly T[], wanted: string): T | undefined {
  const key = wanted.toLowerCase();
  const qualified = tables.find((t) => t.qualified.toLowerCase() === key);
  if (qualified) return qualified;
  const bare = tables.filter((t) => t.name.toLowerCase() === key);
  return bare.length === 1 ? bare[0] : undefined;
}
