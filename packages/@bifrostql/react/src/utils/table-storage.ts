import type { SortOption, TableFilter } from '../types';
import type {
  ColumnPreset,
  FilterPreset,
} from '../hooks/use-bifrost-table.types';
import { sanitizeFilter } from './url-state';

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
    return parsed.filter(
      (p: unknown): p is FilterPreset =>
        typeof p === 'object' &&
        p !== null &&
        'name' in p &&
        'filters' in p &&
        typeof (p as FilterPreset).name === 'string' &&
        typeof (p as FilterPreset).filters === 'object',
    );
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
    return parsed.filter(
      (p: unknown): p is ColumnPreset =>
        typeof p === 'object' &&
        p !== null &&
        'name' in p &&
        'visibleColumns' in p &&
        'columnOrder' in p,
    );
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
