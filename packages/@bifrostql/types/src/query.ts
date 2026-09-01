/**
 * Shared filter, sort, pagination, and query contract types for BifrostQL
 * clients. These mirror the filter syntax accepted by the BifrostQL GraphQL
 * API and are consumed by both `@bifrostql/react` and future clients.
 */

/**
 * A filter object where keys are field names and values are either shorthand
 * equality values or {@link FieldFilter} operator objects.
 *
 * @example
 * ```ts
 * const filter: TableFilter = {
 *   status: 'active',           // shorthand for { _eq: 'active' }
 *   age: { _gte: 18, _lt: 65 },
 * };
 * ```
 */
export interface TableFilter {
  [field: string]: FieldFilter | string | number | boolean | null;
}

/**
 * Operator-based filter for a single field. Follows the Directus filter syntax.
 *
 * @example
 * ```ts
 * const filter: FieldFilter = { _gte: 18, _lt: 65 };
 * ```
 */
export interface FieldFilter {
  /** Equal to. */
  _eq?: string | number | boolean | null;
  /** Not equal to. */
  _neq?: string | number | boolean | null;
  /** Greater than. */
  _gt?: string | number;
  /** Greater than or equal to. */
  _gte?: string | number;
  /** Less than. */
  _lt?: string | number;
  /** Less than or equal to. */
  _lte?: string | number;
  /** Value is in the given array. */
  _in?: Array<string | number>;
  /** Value is not in the given array. */
  _nin?: Array<string | number>;
  /** String contains the given substring (case-sensitive). */
  _contains?: string;
  /** String does not contain the given substring. */
  _ncontains?: string;
  /** String starts with the given prefix. */
  _starts_with?: string;
  /** String ends with the given suffix. */
  _ends_with?: string;
  /** Value is between the two given bounds (inclusive). Translated to `_gte` + `_lte`. */
  _between?: [string | number, string | number];
  /** Value is null. */
  _null?: boolean;
  /** Value is not null. */
  _nnull?: boolean;
}

/**
 * Logical compound filter combining multiple filters with `_and` or `_or`.
 *
 * @example
 * ```ts
 * const filter: CompoundFilter = {
 *   _or: [
 *     { status: 'active' },
 *     { role: { _in: ['admin', 'superadmin'] } },
 *   ],
 * };
 * ```
 */
export interface CompoundFilter {
  /** All child filters must match (logical AND). */
  _and?: Array<TableFilter | CompoundFilter>;
  /** At least one child filter must match (logical OR). */
  _or?: Array<TableFilter | CompoundFilter>;
}

/** A filter that is either a simple {@link TableFilter} or a {@link CompoundFilter}. */
export type AdvancedFilter = TableFilter | CompoundFilter;

/** Offset-based pagination parameters. */
export interface PaginationOptions {
  /** Maximum number of rows to return. */
  limit?: number;
  /** Number of rows to skip before returning results. */
  offset?: number;
}

/**
 * The set of selectable field names for a row type.
 *
 * Resolves to the string keys of `TRow` when a concrete row type is supplied,
 * and falls back to plain `string` for untyped usage (`TRow = unknown` or any
 * row type without statically known keys), so existing untyped call sites keep
 * compiling unchanged.
 */
export type FieldNameOf<TRow> = string &
  ([keyof TRow & string] extends [never] ? string : keyof TRow & string);

/** A single sort directive specifying a field and direction. */
export interface SortOption {
  /** The field name to sort by. */
  field: string;
  /** Sort direction: ascending or descending. */
  direction: 'asc' | 'desc';
}

/**
 * A {@link SortOption} whose `field` is constrained to the keys of a concrete
 * row type. Structurally assignable to `SortOption`, so typed option surfaces
 * interoperate with the untyped query contract. Kept as a separate name (not a
 * generic parameter on `SortOption` itself) so `SortOption<A>`-to-`SortOption`
 * assignments never cross two instantiations of one generic reference — the
 * conditional type in {@link FieldNameOf} makes such pairs invariant.
 */
export interface SortOptionFor<TRow> {
  /** The field name to sort by; a key of `TRow` when `TRow` has known keys. */
  field: FieldNameOf<TRow>;
  /** Sort direction: ascending or descending. */
  direction: 'asc' | 'desc';
}

/**
 * The paged envelope BifrostQL wraps every top-level table query in. Row
 * selections live under `data`; `total` is the unpaged match count. A bare
 * field selection (`{ users { id } }`) is rejected by the server — the
 * table field's GraphQL type is this envelope, not the row type.
 */
export interface PagedResult<TRow = Record<string, unknown>> {
  /** The rows for the requested page. */
  data: TRow[];
  /** Total matching rows before limit/offset. */
  total?: number;
  /** The offset that produced this page. */
  offset?: number;
  /** The limit that produced this page. */
  limit?: number;
}

/**
 * Options for building a table query, combining filters, sorting, pagination,
 * and field selection.
 *
 * @example
 * ```ts
 * const options: QueryOptions = {
 *   filter: { status: 'active' },
 *   sort: [{ field: 'name', direction: 'asc' }],
 *   pagination: { limit: 25, offset: 0 },
 *   fields: ['id', 'name', 'email'],
 * };
 * ```
 */
export interface QueryOptions {
  /** Row filter criteria. */
  filter?: AdvancedFilter;
  /** Sort directives applied in order. */
  sort?: readonly SortOption[];
  /** Pagination parameters (limit/offset). */
  pagination?: PaginationOptions;
  /** Specific fields to select. When omitted, `__typename` is returned. */
  fields?: readonly string[];
}
