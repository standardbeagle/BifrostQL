/**
 * Schema-aware autocomplete for the SQL console, built on `@codemirror/lang-sql`'s
 * `schemaCompletionSource`. That source already implements the hard parts — offering
 * table names after FROM/JOIN and resolving `alias.` to the aliased table's columns by
 * reading the FROM clause out of the syntax tree — so this module's only job is to
 * translate the desktop shell's `BuilderSchema` (fetched once via the Photino
 * `get-builder-schema` bridge) into the flat `{ table: columns }` map that source wants.
 *
 * Tables are keyed by their unqualified name. Against the four providers we target the
 * schema qualifier is almost always the single default schema (sqlite `main`, mssql
 * `dbo`, postgres `public`, a MySQL database), and users write bare table names in the
 * console, so a flat bare-name map matches how the SQL is actually typed. When two
 * schemas expose the same table name their columns are unioned rather than dropped.
 *
 * This is a READ-ONLY view of the cached model; nothing here mutates schema.
 */

import { schemaCompletionSource, sql, type SQLNamespace } from '@codemirror/lang-sql';
import type { CompletionSource } from '@codemirror/autocomplete';
import type { LanguageSupport } from '@codemirror/language';
import type { BuilderSchema } from './builder-bridge';
import { dialectForProvider } from './sql-dialect';
import type { Provider } from '../connection/types';

/**
 * Flattens a {@link BuilderSchema} into the `{ tableName: columnNames[] }` map that
 * `schemaCompletionSource` consumes. Columns are matched to their owning table by the
 * qualified name the bridge stamps on each column.
 */
export function buildSchemaCompletionMap(schema: BuilderSchema): Record<string, string[]> {
  const columnsByQualified = new Map<string, string[]>();
  for (const col of schema.columns) {
    const list = columnsByQualified.get(col.table);
    if (list) list.push(col.name);
    else columnsByQualified.set(col.table, [col.name]);
  }

  const map: Record<string, string[]> = {};
  for (const table of schema.tables) {
    const columns = columnsByQualified.get(table.qualified) ?? [];
    if (table.name in map) {
      // Same bare name in two schemas — union columns so neither table's
      // members are lost to the completion of the other.
      const merged = map[table.name];
      for (const c of columns) if (!merged.includes(c)) merged.push(c);
    } else {
      map[table.name] = [...columns];
    }
  }
  return map;
}

/**
 * The completion source for the given provider + schema, exposed on its own so it can be
 * unit-tested against a mock schema without standing up an editor.
 */
export function createSchemaCompletionSource(
  provider: Provider,
  schema: BuilderSchema,
): CompletionSource {
  return schemaCompletionSource({
    dialect: dialectForProvider(provider),
    schema: buildSchemaCompletionMap(schema) as SQLNamespace,
  });
}

/**
 * The full `LanguageSupport` extension for the editor: dialect-correct highlighting plus
 * the schema-aware completion wired in. Passing it to CodeMirror is all the editor needs
 * for both syntax and autocomplete.
 */
export function createSqlLanguageSupport(
  provider: Provider,
  schema: BuilderSchema,
): LanguageSupport {
  return sql({
    dialect: dialectForProvider(provider),
    schema: buildSchemaCompletionMap(schema) as SQLNamespace,
    upperCaseKeywords: true,
  });
}
