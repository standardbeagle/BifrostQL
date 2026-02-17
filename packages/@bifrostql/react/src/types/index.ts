/** Default TanStack Query options applied to all queries within a {@link BifrostProvider}. */
export interface BifrostDefaultQueryOptions {
  /** Number of retry attempts for failed queries, or `false` to disable retries. */
  retry?: number | false;
  /** Duration in milliseconds that cached data is considered fresh. */
  staleTime?: number;
  /** Duration in milliseconds before unused cached data is garbage-collected. */
  gcTime?: number;
}

/**
 * Configuration for a BifrostQL client instance, provided via {@link BifrostProvider}.
 *
 * @example
 * ```ts
 * const config: BifrostConfig = {
 *   endpoint: 'https://api.example.com/graphql',
 *   headers: { 'X-Custom-Header': 'value' },
 *   getToken: () => localStorage.getItem('token'),
 * };
 * ```
 */
export interface BifrostConfig {
  /** The URL of the BifrostQL GraphQL endpoint. */
  endpoint: string;
  /** Static HTTP headers sent with every request. */
  headers?: Record<string, string>;
  /**
   * Async or sync function that returns a bearer token.
   * Called before each request; the returned value is set as the `Authorization` header.
   */
  getToken?: () => string | null | Promise<string | null>;
  /** Default TanStack Query options applied to all queries. */
  defaultQueryOptions?: BifrostDefaultQueryOptions;
  /** Global error handler invoked on any mutation failure. */
  onError?: (error: Error) => void;
}

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

/** A single sort directive specifying a field and direction. */
export interface SortOption {
  /** The field name to sort by. */
  field: string;
  /** Sort direction: ascending or descending. */
  direction: 'asc' | 'desc';
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
  sort?: SortOption[];
  /** Pagination parameters (limit/offset). */
  pagination?: PaginationOptions;
  /** Specific fields to select. When omitted, `__typename` is returned. */
  fields?: string[];
}
