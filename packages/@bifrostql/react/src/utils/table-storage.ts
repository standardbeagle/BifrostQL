import type { CompoundFilter, SortOption, TableFilter } from '../types';
import type {
  ColumnPreset,
  FilterPreset,
  PinPosition,
} from '../hooks/use-bifrost-table.types';
import { sanitizeFilter } from './url-state';
import { isGraphqlName } from './graphql-identifiers';

export function readSortFromLocalStorage(key: string): SortOption[] | null {
  if (typeof window === 'undefined') return null;
  try {
    const raw = window.localStorage.getItem(key);
    if (!raw) return null;
    const parsed = JSON.parse(raw);
    if (!Array.isArray(parsed)) return null;
    return parsed.filter(
      (s: unknown): s is SortOption =>
        typeof s === 'object' &&
        s !== null &&
        'field' in s &&
        'direction' in s &&
        isGraphqlName((s as SortOption).field) &&
        ((s as SortOption).direction === 'asc' ||
          (s as SortOption).direction === 'desc'),
    );
  } catch {
    return null;
  }
}

export function writeSortToLocalStorage(key: string, sort: SortOption[]): void {
  if (typeof window === 'undefined') return;
  try {
    if (sort.length === 0) {
      window.localStorage.removeItem(key);
    } else {
      window.localStorage.setItem(key, JSON.stringify(sort));
    }
  } catch {
    // localStorage may be unavailable (private browsing, quota exceeded)
  }
}

export function readFiltersFromLocalStorage(key: string): TableFilter | null {
  if (typeof window === 'undefined') return null;
  try {
    const raw = window.localStorage.getItem(`${key}_filters`);
    if (!raw) return null;
    const parsed = JSON.parse(raw);
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed))
      return null;
    // localStorage is attacker-controllable; drop invalid field names at parse
    // so they can't throw later in query construction.
    return sanitizeFilter(parsed as TableFilter);
  } catch {
    return null;
  }
}

export function writeFiltersToLocalStorage(
  key: string,
  filters: TableFilter,
): void {
  if (typeof window === 'undefined') return;
  try {
    if (Object.keys(filters).length === 0) {
      window.localStorage.removeItem(`${key}_filters`);
    } else {
      window.localStorage.setItem(`${key}_filters`, JSON.stringify(filters));
    }
  } catch {
    // localStorage may be unavailable
  }
}

export function readPresetsFromLocalStorage(key: string): FilterPreset[] {
  if (typeof window === 'undefined') return [];
  try {
    const raw = window.localStorage.getItem(`${key}_presets`);
    if (!raw) return [];
    const parsed = JSON.parse(raw);
    if (!Array.isArray(parsed)) return [];
    return parsed
      .map((p: unknown): FilterPreset | null => {
        if (!isRecord(p) || typeof p.name !== 'string') return null;
        if (!isRecord(p.filters)) return null;

        const preset: FilterPreset = {
          name: p.name,
          filters: sanitizeFilter(p.filters as TableFilter),
        };
        const compoundFilter = sanitizeCompoundFilter(p.compoundFilter);
        if (compoundFilter) preset.compoundFilter = compoundFilter;
        return preset;
      })
      .filter((p): p is FilterPreset => p !== null);
  } catch {
    return [];
  }
}

export function writePresetsToLocalStorage(
  key: string,
  presets: FilterPreset[],
): void {
  if (typeof window === 'undefined') return;
  try {
    if (presets.length === 0) {
      window.localStorage.removeItem(`${key}_presets`);
    } else {
      window.localStorage.setItem(`${key}_presets`, JSON.stringify(presets));
    }
  } catch {
    // localStorage may be unavailable
  }
}

export function readColumnPresetsFromLocalStorage(key: string): ColumnPreset[] {
  if (typeof window === 'undefined') return [];
  try {
    const raw = window.localStorage.getItem(`${key}_columnPresets`);
    if (!raw) return [];
    const parsed = JSON.parse(raw);
    if (!Array.isArray(parsed)) return [];
    return parsed
      .map((p: unknown): ColumnPreset | null => {
        if (!isRecord(p) || typeof p.name !== 'string') return null;
        if (!Array.isArray(p.visibleColumns) || !Array.isArray(p.columnOrder))
          return null;

        return {
          name: p.name,
          visibleColumns: p.visibleColumns.filter(isString),
          columnOrder: p.columnOrder.filter(isString),
          columnWidths: sanitizeColumnWidths(p.columnWidths),
          pinnedColumns: sanitizePinnedColumns(p.pinnedColumns),
        };
      })
      .filter((p): p is ColumnPreset => p !== null);
  } catch {
    return [];
  }
}

export function writeColumnPresetsToLocalStorage(
  key: string,
  presets: ColumnPreset[],
): void {
  if (typeof window === 'undefined') return;
  try {
    if (presets.length === 0) {
      window.localStorage.removeItem(`${key}_columnPresets`);
    } else {
      window.localStorage.setItem(
        `${key}_columnPresets`,
        JSON.stringify(presets),
      );
    }
  } catch {
    // localStorage may be unavailable
  }
}

function sanitizeCompoundFilter(value: unknown): CompoundFilter | undefined {
  if (!isRecord(value)) return undefined;

  const clean: CompoundFilter = {};
  const andFilters = sanitizeAdvancedFilterList(value._and);
  const orFilters = sanitizeAdvancedFilterList(value._or);

  if (andFilters.length > 0) clean._and = andFilters;
  if (orFilters.length > 0) clean._or = orFilters;

  return clean._and || clean._or ? clean : undefined;
}

function sanitizeAdvancedFilterList(
  value: unknown,
): Array<TableFilter | CompoundFilter> {
  if (!Array.isArray(value)) return [];
  return value
    .map(sanitizeAdvancedFilter)
    .filter((filter): filter is TableFilter | CompoundFilter => filter !== null);
}

function sanitizeAdvancedFilter(
  value: unknown,
): TableFilter | CompoundFilter | null {
  if (!isRecord(value)) return null;

  if ('_and' in value || '_or' in value) {
    return sanitizeCompoundFilter(value) ?? null;
  }

  const filter = sanitizeFilter(value as TableFilter);
  return Object.keys(filter).length > 0 ? filter : null;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isString(value: unknown): value is string {
  return typeof value === 'string';
}

function sanitizeColumnWidths(value: unknown): Record<string, number> {
  if (!isRecord(value)) return {};

  const widths: Record<string, number> = {};
  for (const [field, width] of Object.entries(value)) {
    if (typeof width === 'number' && Number.isFinite(width)) {
      widths[field] = width;
    }
  }
  return widths;
}

function sanitizePinnedColumns(value: unknown): Record<string, PinPosition> {
  if (!isRecord(value)) return {};

  const pinned: Record<string, PinPosition> = {};
  for (const [field, position] of Object.entries(value)) {
    if (position === 'left' || position === 'right' || position === null) {
      pinned[field] = position;
    }
  }
  return pinned;
}
