import type { SortOption, TableFilter } from '../types';

/** Table state that can be persisted to and restored from URL search parameters. */
export interface UrlTableState {
  /** Current sort directives. */
  sort?: SortOption[];
  /** Current zero-based page index. */
  page?: number;
  /** Number of rows per page. */
  pageSize?: number;
  /** Active table filters. */
  filter?: TableFilter;
}

/**
 * Serialize an array of sort options into a URL-safe string.
 *
 * @param sort - The sort options to serialize.
 * @returns A comma-separated string like `"name:asc,age:desc"`.
 */
export function serializeSort(sort: SortOption[]): string {
  return sort.map((s) => `${s.field}:${s.direction}`).join(',');
}

/**
 * Parse a serialized sort string back into an array of {@link SortOption}.
 *
 * @param raw - A string like `"name:asc,age:desc"` produced by {@link serializeSort}.
 * @returns Parsed sort options, or an empty array if the input is empty or invalid.
 */
export function parseSort(raw: string): SortOption[] {
  if (!raw) return [];
  return raw
    .split(',')
    .map((part) => {
      const [field, direction] = part.split(':');
      if (!field || (direction !== 'asc' && direction !== 'desc')) return null;
      return { field, direction };
    })
    .filter((s): s is SortOption => s !== null);
}

/**
 * Serialize a {@link TableFilter} to a JSON string for URL storage.
 *
 * @param filter - The filter object to serialize.
 * @returns A JSON string representation.
 */
export function serializeFilter(filter: TableFilter): string {
  return JSON.stringify(filter);
}

/**
 * Parse a JSON string back into a {@link TableFilter}.
 *
 * @param raw - A JSON string produced by {@link serializeFilter}.
 * @returns The parsed filter, or `undefined` if the input is empty or invalid.
 */
export function parseFilter(raw: string): TableFilter | undefined {
  if (!raw) return undefined;
  try {
    const parsed = JSON.parse(raw);
    if (
      typeof parsed !== 'object' ||
      parsed === null ||
      Array.isArray(parsed)
    ) {
      return undefined;
    }
    return parsed as TableFilter;
  } catch {
    return undefined;
  }
}

/**
 * Write table state to the URL search parameters using `history.replaceState`.
 *
 * Parameters are prefixed to avoid collisions when multiple tables share a page.
 * Empty values are removed from the URL.
 *
 * @param state - The table state to persist.
 * @param prefix - A string prefix for the URL parameter names (e.g. `"users"`).
 */
export function writeToUrl(state: UrlTableState, prefix: string): void {
  const url = new URL(window.location.href);

  if (state.sort && state.sort.length > 0) {
    url.searchParams.set(`${prefix}_sort`, serializeSort(state.sort));
  } else {
    url.searchParams.delete(`${prefix}_sort`);
  }

  if (state.page !== undefined && state.page > 0) {
    url.searchParams.set(`${prefix}_page`, String(state.page));
  } else {
    url.searchParams.delete(`${prefix}_page`);
  }

  if (state.pageSize !== undefined) {
    url.searchParams.set(`${prefix}_size`, String(state.pageSize));
  } else {
    url.searchParams.delete(`${prefix}_size`);
  }

  if (state.filter && Object.keys(state.filter).length > 0) {
    url.searchParams.set(`${prefix}_filter`, serializeFilter(state.filter));
  } else {
    url.searchParams.delete(`${prefix}_filter`);
  }

  window.history.replaceState(window.history.state, '', url.toString());
}

/**
 * Read table state from the current URL search parameters.
 *
 * Returns an empty object when running on the server (no `window`).
 *
 * @param prefix - The parameter prefix used when writing (must match {@link writeToUrl}).
 * @returns The parsed table state, with only the parameters that were present.
 */
export function readFromUrl(prefix: string): UrlTableState {
  if (typeof window === 'undefined') return {};

  const params = new URLSearchParams(window.location.search);
  const state: UrlTableState = {};

  const sortRaw = params.get(`${prefix}_sort`);
  if (sortRaw) {
    const parsed = parseSort(sortRaw);
    if (parsed.length > 0) state.sort = parsed;
  }

  const pageRaw = params.get(`${prefix}_page`);
  if (pageRaw) {
    const parsed = parseInt(pageRaw, 10);
    if (!isNaN(parsed) && parsed >= 0) state.page = parsed;
  }

  const sizeRaw = params.get(`${prefix}_size`);
  if (sizeRaw) {
    const parsed = parseInt(sizeRaw, 10);
    if (!isNaN(parsed) && parsed > 0) state.pageSize = parsed;
  }

  const filterRaw = params.get(`${prefix}_filter`);
  if (filterRaw) {
    const parsed = parseFilter(filterRaw);
    if (parsed) state.filter = parsed;
  }

  return state;
}
