import type { SortOption, TableFilter } from '../types';

export interface UrlTableState {
  sort?: SortOption[];
  page?: number;
  pageSize?: number;
  filter?: TableFilter;
}

export function serializeSort(sort: SortOption[]): string {
  return sort.map((s) => `${s.field}:${s.direction}`).join(',');
}

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

export function serializeFilter(filter: TableFilter): string {
  return JSON.stringify(filter);
}

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
