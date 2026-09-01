import type { AdvancedFilter, PagedResult, SortOption } from '@bifrostql/types';
import { buildGraphqlQuery } from './query-builder';
import {
  buildInsertMutation,
  buildUpdateMutation,
  buildDeleteMutation,
} from './mutation-builder';

/**
 * Typed CRUD helper layer for entity screens.
 *
 * This module layers a thin, type-parameterized ergonomics surface over the
 * existing string builders (`query-builder.ts` / `mutation-builder.ts`). It
 * does **not** re-implement any GraphQL string construction — every helper
 * delegates to a builder and only adds compile-time types describing the
 * inputs and the inferred result shape.
 *
 * Parameterize the helpers with the generated row/insert/update types from
 * `@bifrostql/types/generated`, e.g. `UserRow`.
 */

/** Field-name keys of a generated row type. */
export type RowField<TRow> = Extract<keyof TRow, string>;

/**
 * Identifies a single row.
 *
 * A scalar is shorthand for a table with one primary-key column. Tables with a
 * composite key must pass an object naming every key column — BigInt and
 * Decimal key values should be carried as decimal strings so a JS number
 * coercion cannot round away the trailing digits.
 */
export type RowKey = string | number | Record<string, unknown>;

/** Configuration for {@link createCrudHelpers}. */
export interface CrudHelpersConfig {
  /**
   * The table's primary-key columns, in order. Defaults to `['id']` — the
   * convenience path for the common single-`id` table. Tables with a renamed
   * or composite primary key MUST declare it here, or `detail`, `update`, and
   * `delete` will target the wrong column.
   */
  primaryKeys?: readonly string[];
}

/** A built CRUD operation: the GraphQL string plus a phantom result type. */
export interface TypedOperation<TResult> {
  /** The GraphQL query or mutation string, produced by the underlying builder. */
  readonly query: string;
  /**
   * Phantom marker carrying the inferred result type. Always `undefined` at
   * runtime — present only so `typeof op.__result` resolves to `TResult` for
   * type-level assertions and downstream inference.
   */
  readonly __result?: TResult;
}

/** Options for a {@link CrudHelpers.list} query. */
export interface ListOptions<TRow> {
  /** Row filter criteria. */
  filter?: AdvancedFilter;
  /** Sort directives applied in order. */
  sort?: SortOption[];
  /** Maximum number of rows to return. */
  limit?: number;
  /** Number of rows to skip before returning results. */
  offset?: number;
  /**
   * Fields to select. Constrained to keys of the row type. When omitted, all
   * row fields are selected.
   */
  fields?: ReadonlyArray<RowField<TRow>>;
}

/** Options for a {@link CrudHelpers.detail} query. */
export interface DetailOptions<TRow> {
  /** Fields to select. When omitted, all row fields are selected. */
  fields?: ReadonlyArray<RowField<TRow>>;
}

/** Options for a {@link CrudHelpers.lookup} query (FK-selector style). */
export interface LookupOptions<TRow> {
  /** The value field returned for each option (typically the primary key). */
  valueField: RowField<TRow>;
  /** The human-readable label field returned for each option. */
  labelField: RowField<TRow>;
  /** Optional filter narrowing the candidate rows. */
  filter?: AdvancedFilter;
  /** Maximum number of options to return. */
  limit?: number;
}

/**
 * The six typed CRUD helpers for a single entity table, returned by
 * {@link createCrudHelpers}.
 *
 * @typeParam TRow - The generated row type (e.g. `UserRow`).
 * @typeParam TInsert - The insert input type. Defaults to `Partial<TRow>`.
 * @typeParam TUpdate - The update input type. Defaults to `Partial<TRow>`.
 */
export interface CrudHelpers<
  TRow,
  TInsert = Partial<TRow>,
  TUpdate = Partial<TRow>,
> {
  /** Build a list query. Result type: an array of (selected) rows. */
  list(options?: ListOptions<TRow>): TypedOperation<PagedResult<TRow>>;
  /** Build a detail-by-key query. Result type: a single row or `null`. */
  detail(
    key: RowKey,
    options?: DetailOptions<TRow>,
  ): TypedOperation<PagedResult<TRow>>;
  /** Build a create mutation. The `input` is typed; result type: the created row. */
  create(input: TInsert): TypedCreateOperation<TRow, TInsert>;
  /** Build an update mutation. The `changes` are typed; result type: the updated row. */
  update(key: RowKey, changes: TUpdate): TypedUpdateOperation<TRow, TUpdate>;
  /** Build a delete mutation. Result type: the deleted row's key. */
  delete(key: RowKey): TypedDeleteOperation;
  /** Build a narrow FK-selector lookup query. Result type: `{ value, label }` options. */
  lookup(
    options: LookupOptions<TRow>,
  ): TypedOperation<PagedResult<{ value: unknown; label: unknown }>>;
}

/** A create operation: carries the typed mutation variables alongside the string. */
export interface TypedCreateOperation<
  TRow,
  TInsert,
> extends TypedOperation<TRow> {
  /** The `$detail` variable payload for the mutation, typed as `TInsert`. */
  readonly variables: { detail: TInsert };
}

/** An update operation: carries the typed mutation variables alongside the string. */
export interface TypedUpdateOperation<
  TRow,
  TUpdate,
> extends TypedOperation<TRow> {
  /**
   * The `$detail` variable payload for the mutation: the typed changes merged
   * with every primary-key column identifying the row.
   */
  readonly variables: { detail: TUpdate & Record<string, unknown> };
}

/** A delete operation: carries the row-key variables alongside the string. */
export interface TypedDeleteOperation extends TypedOperation<
  Record<string, unknown>
> {
  /** The `$detail` variable payload identifying the row to delete. */
  readonly variables: { detail: Record<string, unknown> };
}

/** The conventional single primary-key column, used when none is configured. */
const DEFAULT_PRIMARY_KEYS: readonly string[] = ['id'];

/**
 * Resolve a caller-supplied row key into a complete `{ column: value }` map
 * covering every primary-key column.
 *
 * A scalar is only accepted for a single-column key — guessing which column a
 * scalar meant on a composite key would silently target the wrong rows, so it
 * throws instead. A key object must supply every column for the same reason.
 */
function resolveRowKey(
  table: string,
  primaryKeys: readonly string[],
  key: RowKey,
): Record<string, unknown> {
  if (typeof key === 'object' && key !== null) {
    const missing = primaryKeys.filter((column) => key[column] === undefined);
    if (missing.length > 0) {
      throw new Error(
        `Cannot identify a row in "${table}": key is missing primary-key column(s) ${missing.join(', ')}.`,
      );
    }
    return Object.fromEntries(
      primaryKeys.map((column) => [column, key[column]]),
    );
  }

  if (primaryKeys.length !== 1) {
    throw new Error(
      `Cannot identify a row in "${table}": it has a composite primary key (${primaryKeys.join(', ')}), so pass an object such as { ${primaryKeys.join(': …, ')}: … } instead of a single value.`,
    );
  }

  return { [primaryKeys[0]]: key };
}

/** Build an AND-of-equalities filter covering every primary-key column. */
function keyFilter(keyValues: Record<string, unknown>): AdvancedFilter {
  const columns = Object.keys(keyValues);
  if (columns.length === 1) {
    return { [columns[0]]: { _eq: keyValues[columns[0]] } };
  }
  return {
    _and: columns.map((column) => ({ [column]: { _eq: keyValues[column] } })),
  };
}

/**
 * Create a set of typed CRUD helpers for a single entity table.
 *
 * Parameterize with the generated row type (and optionally distinct insert /
 * update input types). Each returned helper delegates to an existing string
 * builder — no GraphQL string construction is duplicated here.
 *
 * @typeParam TRow - The generated row type (e.g. `UserRow`).
 * @typeParam TInsert - The insert input type. Defaults to `Partial<TRow>`.
 * @typeParam TUpdate - The update input type. Defaults to `Partial<TRow>`.
 * @param table - The database table name.
 * @returns A {@link CrudHelpers} object with `list`, `detail`, `create`,
 *   `update`, `delete`, and `lookup`.
 *
 * @example
 * ```ts
 * import type { UserRow } from '@bifrostql/types/generated';
 *
 * const users = createCrudHelpers<UserRow>('users');
 * const op = users.list({ fields: ['id', 'name'], limit: 25 });
 * // op.query   -> GraphQL string
 * // typeof op.__result -> UserRow[]
 * ```
 */
export function createCrudHelpers<
  TRow,
  TInsert = Partial<TRow>,
  TUpdate = Partial<TRow>,
>(
  table: string,
  config: CrudHelpersConfig = {},
): CrudHelpers<TRow, TInsert, TUpdate> {
  const primaryKeys = config.primaryKeys ?? DEFAULT_PRIMARY_KEYS;
  if (primaryKeys.length === 0) {
    throw new Error(
      `createCrudHelpers("${table}"): primaryKeys must name at least one column.`,
    );
  }

  return {
    list(options: ListOptions<TRow> = {}): TypedOperation<PagedResult<TRow>> {
      const { filter, sort, limit, offset, fields } = options;
      const query = buildGraphqlQuery(table, {
        filter,
        sort,
        pagination:
          limit !== undefined || offset !== undefined
            ? { limit, offset }
            : undefined,
        fields: fields ? [...fields] : undefined,
      });
      return { query };
    },

    detail(
      key: RowKey,
      options: DetailOptions<TRow> = {},
    ): TypedOperation<PagedResult<TRow>> {
      const query = buildGraphqlQuery(table, {
        filter: keyFilter(resolveRowKey(table, primaryKeys, key)),
        pagination: { limit: 1 },
        fields: options.fields ? [...options.fields] : undefined,
      });
      return { query };
    },

    create(input: TInsert): TypedCreateOperation<TRow, TInsert> {
      return {
        query: buildInsertMutation(table),
        variables: { detail: input },
      };
    },

    update(key: RowKey, changes: TUpdate): TypedUpdateOperation<TRow, TUpdate> {
      return {
        query: buildUpdateMutation(table),
        variables: {
          detail: { ...changes, ...resolveRowKey(table, primaryKeys, key) },
        },
      };
    },

    delete(key: RowKey): TypedDeleteOperation {
      return {
        query: buildDeleteMutation(table),
        variables: { detail: resolveRowKey(table, primaryKeys, key) },
      };
    },

    lookup(
      options: LookupOptions<TRow>,
    ): TypedOperation<PagedResult<{ value: unknown; label: unknown }>> {
      const { valueField, labelField, filter, limit } = options;
      const query = buildGraphqlQuery(table, {
        filter,
        pagination: limit !== undefined ? { limit } : undefined,
        fields: [valueField, labelField],
      });
      return { query };
    },
  };
}
