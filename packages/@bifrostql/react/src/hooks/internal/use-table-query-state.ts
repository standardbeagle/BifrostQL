import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { readFromUrl, writeToUrl } from '../../utils/url-state';
import { canAccessWindow } from '../../utils/table-breakpoints';
import { countActiveFilters } from '../../utils/table-client-ops';
import {
  readFiltersFromLocalStorage,
  readPresetsFromLocalStorage,
  readSortFromLocalStorage,
  writeFiltersToLocalStorage,
  writePresetsToLocalStorage,
  writeSortToLocalStorage,
} from '../../utils/table-storage';
import type { SortOption, TableFilter, CompoundFilter } from '../../types';
import type {
  ColumnConfig,
  FilterPreset,
  FilterState,
  LocalStorageConfig,
  PaginationState,
  SortDirection,
  SortState,
} from '../use-bifrost-table.types';

interface ResolvedUrlSyncConfig {
  enabled: boolean;
  prefix: string;
  debounceMs: number;
}

export interface UseTableQueryStateOptions {
  columns: ColumnConfig[];
  multiSort: boolean;
  defaultSort: SortOption[];
  defaultFilters: TableFilter;
  syncConfig: ResolvedUrlSyncConfig;
  localStorageConfig: LocalStorageConfig | undefined;
  initialPageSize: number;
  filterDebounceMs: number;
}

export interface UseTableQueryStateResult {
  sort: SortOption[];
  filters: TableFilter;
  debouncedFilters: TableFilter;
  compoundFilter: CompoundFilter | null;
  page: number;
  pageSize: number;
  activeFilterCount: number;
  sorting: SortState;
  filtersApi: FilterState;
  pagination: PaginationState;
}

/**
 * Owns the core query-driving state: sorting, filtering (with debounce and
 * compound filters), pagination, and filter presets. Wires URL synchronization,
 * browser history (popstate), and `localStorage` persistence.
 */
export function useTableQueryState({
  columns,
  multiSort,
  defaultSort,
  defaultFilters,
  syncConfig,
  localStorageConfig,
  initialPageSize,
  filterDebounceMs,
}: UseTableQueryStateOptions): UseTableQueryStateResult {
  const initialUrlState = useMemo(() => {
    if (!syncConfig.enabled || !canAccessWindow()) return null;
    return readFromUrl(syncConfig.prefix);
    // Only read URL on mount
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const initialLocalStorageSort = useMemo(() => {
    if (!localStorageConfig?.key) return null;
    return readSortFromLocalStorage(localStorageConfig.key);
    // Only read localStorage on mount
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const initialLocalStorageFilters = useMemo(() => {
    if (!localStorageConfig?.key || !localStorageConfig.persistFilters)
      return null;
    return readFiltersFromLocalStorage(localStorageConfig.key);
    // Only read localStorage on mount
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const initialPresets = useMemo(() => {
    if (!localStorageConfig?.key) return [];
    return readPresetsFromLocalStorage(localStorageConfig.key);
    // Only read localStorage on mount
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const [sort, setSort] = useState<SortOption[]>(
    initialUrlState?.sort ?? initialLocalStorageSort ?? defaultSort,
  );
  const [filters, setFilters] = useState<TableFilter>(
    initialUrlState?.filter ?? initialLocalStorageFilters ?? defaultFilters,
  );
  const [debouncedFilters, setDebouncedFilters] = useState<TableFilter>(
    initialUrlState?.filter ?? initialLocalStorageFilters ?? defaultFilters,
  );
  const [compoundFilter, setCompoundFilterState] =
    useState<CompoundFilter | null>(null);
  const [presets, setPresets] = useState<FilterPreset[]>(initialPresets);
  const [page, setPage] = useState(initialUrlState?.page ?? 0);
  const [pageSize, setPageSizeState] = useState(
    initialUrlState?.pageSize ?? initialPageSize,
  );

  const urlDebounceTimerRef = useRef<ReturnType<typeof setTimeout>>();
  const filterDebounceTimerRef = useRef<ReturnType<typeof setTimeout>>();
  const columnFields = useMemo(
    () => columns.map((column) => column.field),
    [columns],
  );
  const previousColumnFieldsRef = useRef(columnFields);

  useEffect(() => {
    const previousFields = previousColumnFieldsRef.current;
    const currentFieldSet = new Set(columnFields);
    const removedFieldSet = new Set(
      previousFields.filter((field) => !currentFieldSet.has(field)),
    );

    if (removedFieldSet.size === 0) {
      previousColumnFieldsRef.current = columnFields;
      return;
    }

    setSort((prev) => {
      const next = prev.filter((entry) => !removedFieldSet.has(entry.field));
      return sortOptionsEqual(prev, next) ? prev : next;
    });

    setFilters((prev) => {
      const next = pruneRemovedTableFilter(prev, removedFieldSet);
      return shallowFilterEqual(prev, next) ? prev : next;
    });

    setDebouncedFilters((prev) => {
      const next = pruneRemovedTableFilter(prev, removedFieldSet);
      return shallowFilterEqual(prev, next) ? prev : next;
    });

    setCompoundFilterState((prev) =>
      prev ? pruneRemovedCompoundFilter(prev, removedFieldSet) : prev,
    );

    previousColumnFieldsRef.current = columnFields;
  }, [columnFields]);

  // Debounce filter changes before sending to server
  useEffect(() => {
    if (filterDebounceTimerRef.current) {
      clearTimeout(filterDebounceTimerRef.current);
    }

    filterDebounceTimerRef.current = setTimeout(() => {
      setDebouncedFilters(filters);
    }, filterDebounceMs);

    return () => {
      if (filterDebounceTimerRef.current) {
        clearTimeout(filterDebounceTimerRef.current);
      }
    };
  }, [filters, filterDebounceMs]);

  useEffect(() => {
    if (!syncConfig.enabled || !canAccessWindow()) return;

    if (urlDebounceTimerRef.current) {
      clearTimeout(urlDebounceTimerRef.current);
    }

    urlDebounceTimerRef.current = setTimeout(() => {
      writeToUrl(
        { sort, page, pageSize, filter: debouncedFilters },
        syncConfig.prefix,
      );
    }, syncConfig.debounceMs);

    return () => {
      if (urlDebounceTimerRef.current) {
        clearTimeout(urlDebounceTimerRef.current);
      }
    };
  }, [
    sort,
    page,
    pageSize,
    debouncedFilters,
    syncConfig.enabled,
    syncConfig.prefix,
    syncConfig.debounceMs,
  ]);

  useEffect(() => {
    if (!syncConfig.enabled || !canAccessWindow()) return;

    const handlePopState = () => {
      const urlState = readFromUrl(syncConfig.prefix);
      setSort(urlState.sort ?? defaultSort);
      setFilters(urlState.filter ?? defaultFilters);
      setDebouncedFilters(urlState.filter ?? defaultFilters);
      setPage(urlState.page ?? 0);
      if (urlState.pageSize) setPageSizeState(urlState.pageSize);
    };

    window.addEventListener('popstate', handlePopState);
    return () => window.removeEventListener('popstate', handlePopState);
  }, [syncConfig.enabled, syncConfig.prefix, defaultSort, defaultFilters]);

  useEffect(() => {
    if (!localStorageConfig?.key) return;
    writeSortToLocalStorage(localStorageConfig.key, sort);
  }, [sort, localStorageConfig?.key]);

  useEffect(() => {
    if (!localStorageConfig?.key || !localStorageConfig.persistFilters) return;
    writeFiltersToLocalStorage(localStorageConfig.key, filters);
  }, [filters, localStorageConfig?.key, localStorageConfig?.persistFilters]);

  const activeFilterCount = useMemo(
    () => countActiveFilters(filters, compoundFilter),
    [filters, compoundFilter],
  );

  // --- Sorting actions ---
  const toggleSort = useCallback(
    (field: string, multi?: boolean) => {
      const col = columns.find((c) => c.field === field);
      if (!col?.sortable) return;

      const useMulti = multi ?? multiSort;

      setSort((prev) => {
        const existing = prev.find((s) => s.field === field);

        if (!existing) {
          const newSort: SortOption = { field, direction: 'asc' };
          return useMulti ? [...prev, newSort] : [newSort];
        }

        if (existing.direction === 'asc') {
          const updated = prev.map((s) =>
            s.field === field ? { ...s, direction: 'desc' as const } : s,
          );
          return updated;
        }

        return prev.filter((s) => s.field !== field);
      });
      setPage(0);
    },
    [columns, multiSort],
  );

  const addSort = useCallback(
    (field: string, direction: SortDirection) => {
      const col = columns.find((c) => c.field === field);
      if (!col?.sortable) return;

      setSort((prev) => {
        const filtered = prev.filter((s) => s.field !== field);
        return [...filtered, { field, direction }];
      });
      setPage(0);
    },
    [columns],
  );

  const removeSort = useCallback((field: string) => {
    setSort((prev) => prev.filter((s) => s.field !== field));
    setPage(0);
  }, []);

  const clearSort = useCallback(() => {
    setSort([]);
    setPage(0);
  }, []);

  const getSortIndicator = useCallback(
    (field: string): string => {
      const entry = sort.find((s) => s.field === field);
      if (!entry) return '';
      return entry.direction === 'asc' ? '▲' : '▼';
    },
    [sort],
  );

  const getSortPriority = useCallback(
    (field: string): number => {
      const index = sort.findIndex((s) => s.field === field);
      return index === -1 ? -1 : index + 1;
    },
    [sort],
  );

  const handleSetSorting = useCallback((newSort: SortOption[]) => {
    setSort(newSort);
    setPage(0);
  }, []);

  // --- Filter actions ---
  const setColumnFilter = useCallback(
    (field: string, value: TableFilter[string]) => {
      setFilters((prev) => {
        if (
          value === null ||
          value === undefined ||
          value === '' ||
          (typeof value === 'object' && Object.keys(value).length === 0)
        ) {
          return Object.fromEntries(
            Object.entries(prev).filter(([key]) => key !== field),
          );
        }
        return { ...prev, [field]: value };
      });
      setPage(0);
    },
    [],
  );

  const clearFilters = useCallback(() => {
    setFilters({});
    setCompoundFilterState(null);
    setPage(0);
  }, []);

  const handleSetFilters = useCallback((newFilters: TableFilter) => {
    setFilters(newFilters);
    setPage(0);
  }, []);

  const setCompoundFilter = useCallback((filter: CompoundFilter | null) => {
    setCompoundFilterState(filter);
    setPage(0);
  }, []);

  const savePreset = useCallback(
    (name: string) => {
      setPresets((prev) => {
        const existing = prev.findIndex((p) => p.name === name);
        const preset: FilterPreset = {
          name,
          filters: { ...filters },
          compoundFilter: compoundFilter ?? undefined,
        };
        const updated =
          existing >= 0
            ? prev.map((p, i) => (i === existing ? preset : p))
            : [...prev, preset];
        if (localStorageConfig?.key) {
          writePresetsToLocalStorage(localStorageConfig.key, updated);
        }
        return updated;
      });
    },
    [filters, compoundFilter, localStorageConfig?.key],
  );

  const loadPreset = useCallback(
    (name: string) => {
      const preset = presets.find((p) => p.name === name);
      if (!preset) return;
      setFilters(preset.filters);
      setCompoundFilterState(preset.compoundFilter ?? null);
      setPage(0);
    },
    [presets],
  );

  const deletePreset = useCallback(
    (name: string) => {
      setPresets((prev) => {
        const updated = prev.filter((p) => p.name !== name);
        if (localStorageConfig?.key) {
          writePresetsToLocalStorage(localStorageConfig.key, updated);
        }
        return updated;
      });
    },
    [localStorageConfig?.key],
  );

  // --- Pagination actions ---
  const handleSetPage = useCallback((newPage: number) => {
    if (newPage < 0) return;
    setPage(newPage);
  }, []);

  const handleSetPageSize = useCallback((size: number) => {
    setPageSizeState(size);
    setPage(0);
  }, []);

  const nextPage = useCallback(() => {
    setPage((prev) => prev + 1);
  }, []);

  const previousPage = useCallback(() => {
    setPage((prev) => Math.max(0, prev - 1));
  }, []);

  return {
    sort,
    filters,
    debouncedFilters,
    compoundFilter,
    page,
    pageSize,
    activeFilterCount,
    sorting: {
      current: sort,
      setSorting: handleSetSorting,
      toggleSort,
      addSort,
      removeSort,
      clearSort,
      getSortIndicator,
      getSortPriority,
    },
    filtersApi: {
      current: filters,
      compoundFilter,
      activeFilterCount,
      setFilters: handleSetFilters,
      setColumnFilter,
      setCompoundFilter,
      clearFilters,
      presets,
      savePreset,
      loadPreset,
      deletePreset,
    },
    pagination: {
      page,
      pageSize,
      setPage: handleSetPage,
      setPageSize: handleSetPageSize,
      nextPage,
      previousPage,
    },
  };
}

function sortOptionsEqual(left: SortOption[], right: SortOption[]): boolean {
  return (
    left.length === right.length &&
    left.every(
      (entry, index) =>
        entry.field === right[index].field &&
        entry.direction === right[index].direction,
    )
  );
}

function shallowFilterEqual(left: TableFilter, right: TableFilter): boolean {
  const leftKeys = Object.keys(left);
  const rightKeys = Object.keys(right);
  return (
    leftKeys.length === rightKeys.length &&
    leftKeys.every((key) => Object.is(left[key], right[key]))
  );
}

function pruneRemovedTableFilter(
  filter: TableFilter,
  removedFieldSet: ReadonlySet<string>,
): TableFilter {
  return Object.fromEntries(
    Object.entries(filter).filter(([field]) => !removedFieldSet.has(field)),
  );
}

function pruneRemovedCompoundFilter(
  filter: CompoundFilter,
  removedFieldSet: ReadonlySet<string>,
): CompoundFilter | null {
  const next: CompoundFilter = {};
  const andFilters = filter._and
    ?.map((child) => pruneRemovedAdvancedFilter(child, removedFieldSet))
    .filter((child): child is TableFilter | CompoundFilter => child !== null);
  const orFilters = filter._or
    ?.map((child) => pruneRemovedAdvancedFilter(child, removedFieldSet))
    .filter((child): child is TableFilter | CompoundFilter => child !== null);

  if (andFilters?.length) next._and = andFilters;
  if (orFilters?.length) next._or = orFilters;

  return next._and || next._or ? next : null;
}

function pruneRemovedAdvancedFilter(
  filter: TableFilter | CompoundFilter,
  removedFieldSet: ReadonlySet<string>,
): TableFilter | CompoundFilter | null {
  if (isCompoundFilter(filter)) {
    return pruneRemovedCompoundFilter(filter, removedFieldSet);
  }

  const next = pruneRemovedTableFilter(filter, removedFieldSet);
  return Object.keys(next).length > 0 ? next : null;
}

function isCompoundFilter(
  filter: TableFilter | CompoundFilter,
): filter is CompoundFilter {
  return '_and' in filter || '_or' in filter;
}
