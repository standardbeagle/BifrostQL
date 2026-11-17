/**
 * The column(s) that identify a row: one column name, or every column of a
 * composite primary key in key order.
 */
export type RowKeyColumns = string | readonly string[];

/** The key columns as a list, whichever form they were given in. */
export function rowKeyColumnList(rowKey: RowKeyColumns): readonly string[] {
  return typeof rowKey === 'string' ? [rowKey] : rowKey;
}

/**
 * A stable string identity for `row`, used to key selection, editing,
 * expansion and React lists.
 *
 * A single column keeps its plain `String(value)` form. A composite key
 * encodes every column's value, in key order, as a JSON array, so
 * `(1, 23)` and `(12, 3)` never collide. A composite row missing a key column
 * throws: guessing from the columns that are present would merge distinct
 * rows.
 */
export function rowIdentity(
  row: Record<string, unknown>,
  rowKey: RowKeyColumns,
): string {
  if (typeof rowKey === 'string') return String(row[rowKey]);
  if (rowKey.length === 0) {
    throw new Error('rowKey must name at least one column.');
  }
  const missing = rowKey.filter((column) => row[column] === undefined);
  if (missing.length > 0) {
    throw new Error(
      `Cannot identify a row: it is missing key column(s) ${missing.join(', ')}. Select every rowKey column.`,
    );
  }
  return JSON.stringify(rowKey.map((column) => row[column]));
}

/**
 * Identifies one row by its key: a scalar for a single-column key, or an
 * object naming every key column. BigInt and Decimal key values should be
 * decimal strings so a JS number cannot round away trailing digits.
 */
export type RowKeyValue = string | number | Record<string, unknown>;

/**
 * Resolve a caller-supplied row key into a `{ column: value }` map covering
 * every key column.
 *
 * A scalar is only accepted for a single-column key: guessing which column a
 * scalar meant on a composite key would target the wrong rows, so it throws
 * instead. A key object must supply every column for the same reason.
 */
export function resolveRowKey(
  table: string,
  keyColumns: RowKeyColumns,
  key: RowKeyValue,
): Record<string, unknown> {
  const columns = rowKeyColumnList(keyColumns);
  if (columns.length === 0) {
    throw new Error(
      `Cannot identify a row in "${table}": no key columns are configured.`,
    );
  }
  if (typeof key === 'object' && key !== null) {
    const missing = columns.filter((column) => key[column] === undefined);
    if (missing.length > 0) {
      throw new Error(
        `Cannot identify a row in "${table}": key is missing primary-key column(s) ${missing.join(', ')}.`,
      );
    }
    return Object.fromEntries(columns.map((column) => [column, key[column]]));
  }
  if (columns.length !== 1) {
    throw new Error(
      `Cannot identify a row in "${table}": it has a composite primary key (${columns.join(', ')}), so pass an object such as { ${columns.join(': …, ')}: … } instead of a single value.`,
    );
  }
  return { [columns[0]]: key };
}

/** True when `a` and `b` have the same value in every key column. */
export function sameRowKey(
  a: Record<string, unknown>,
  b: Record<string, unknown>,
  rowKey: RowKeyColumns,
): boolean {
  return rowKeyColumnList(rowKey).every((column) => a[column] === b[column]);
}

/**
 * An AND-of-equalities filter matching `keyColumns` of a related table to the
 * values `parentRow` holds in `parentColumns`, pairing columns by position.
 * The two lists must be the same length: a composite FK joined on fewer
 * columns would match rows of other parents.
 */
export function keyEqualityFilter(
  parentRow: Record<string, unknown>,
  parentColumns: RowKeyColumns,
  keyColumns: RowKeyColumns,
): Record<string, unknown> {
  const parents = rowKeyColumnList(parentColumns);
  const keys = rowKeyColumnList(keyColumns);
  if (parents.length === 0 || parents.length !== keys.length) {
    throw new Error(
      `Cannot join on key columns: parent key (${parents.join(', ')}) and child key (${keys.join(', ')}) must name the same number of columns.`,
    );
  }
  const terms = keys.map((column, i) => {
    const value = parentRow[parents[i]];
    if (value === undefined) {
      throw new Error(
        `Cannot join on key columns: the parent row is missing ${parents[i]}.`,
      );
    }
    return { [column]: { _eq: value } };
  });
  return terms.length === 1 ? terms[0] : { _and: terms };
}
