import type { AdvancedFilter, SortOption } from '@bifrostql/types';
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
  list(options?: ListOptions<TRow>): TypedOperation<TRow[]>;
  /** Build a detail-by-id query. Result type: a single row or `null`. */
  detail(
    id: string | number,
    options?: DetailOptions<TRow>,
  ): TypedOperation<TRow | null>;
  /** Build a create mutation. The `input` is typed; result type: the created row. */
  create(input: TInsert): TypedCreateOperation<TRow, TInsert>;
  /** Build an update mutation. The `changes` are typed; result type: the updated row. */
  update(
    id: string | number,
    changes: TUpdate,
  ): TypedUpdateOperation<TRow, TUpdate>;
  /** Build a delete mutation. Result type: the deleted row's id. */
  delete(id: string | number): TypedDeleteOperation;
  /** Build a narrow FK-selector lookup query. Result type: `{ value, label }` options. */
  lookup(
    options: LookupOptions<TRow>,
  ): TypedOperation<Array<{ value: unknown; label: unknown }>>;
}

/** A create operation: carries the typed mutation variables alongside the string. */
export interface TypedCreateOperation<TRow, TInsert>
  extends TypedOperation<TRow> {
  /** The `$detail` variable payload for the mutation, typed as `TInsert`. */
  readonly variables: { detail: TInsert };
}

/** An update operation: carries the typed mutation variables alongside the string. */
export interface TypedUpdateOperation<TRow, TUpdate>
  extends TypedOperation<TRow> {
  /** The `$detail` variable payload for the mutation, typed as `TUpdate` plus the id. */
  readonly variables: { detail: TUpdate & { id: string | number } };
}

/** A delete operation: carries the id variable alongside the string. */
export interface TypedDeleteOperation
  extends TypedOperation<{ id: string | number }> {
  /** The `$detail` variable payload identifying the row to delete. */
  readonly variables: { detail: { id: string | number } };
}

/** The conventional id field name for detail/lookup primary-key filtering. */
const ID_FIELD = 'id';

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
>(table: string): CrudHelpers<TRow, TInsert, TUpdate> {
  return {
    list(options: ListOptions<TRow> = {}): TypedOperation<TRow[]> {
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
      id: string | number,
      options: DetailOptions<TRow> = {},
    ): TypedOperation<TRow | null> {
      const query = buildGraphqlQuery(table, {
        filter: { [ID_FIELD]: { _eq: id } },
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

    update(
      id: string | number,
      changes: TUpdate,
    ): TypedUpdateOperation<TRow, TUpdate> {
      return {
        query: buildUpdateMutation(table),
        variables: { detail: { ...changes, id } },
      };
    },

    delete(id: string | number): TypedDeleteOperation {
      return {
        query: buildDeleteMutation(table),
        variables: { detail: { id } },
      };
    },

    lookup(
      options: LookupOptions<TRow>,
    ): TypedOperation<Array<{ value: unknown; label: unknown }>> {
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
