import { useState, useCallback, useMemo, useEffect, useRef } from 'react';
import { useBifrostQuery } from './use-bifrost-query';
import { readFromUrl, writeToUrl } from '../utils/url-state';
import type { UseBifrostOptions } from './use-bifrost';
import type { SortOption, TableFilter } from '../types';

export type AggregateFn = 'sum' | 'avg' | 'min' | 'max' | 'count';

export interface AggregateConfig {
  field?: string;
  fn: AggregateFn | ((values: unknown[]) => unknown);
}

export interface ColumnConfig {
  field: string;
  header: string;
  width?: number;
  sortable?: boolean;
  filterable?: boolean;
  computed?: (row: Record<string, unknown>) => unknown;
}

export interface SortState {
  current: SortOption[];
  setSorting: (sort: SortOption[]) => void;
  toggleSort: (field: string) => void;
}

export interface FilterState {
  current: TableFilter;
  setFilters: (filters: TableFilter) => void;
  setColumnFilter: (field: string, value: TableFilter[string]) => void;
  clearFilters: () => void;
}

export interface PaginationState {
  page: number;
  pageSize: number;
  setPage: (page: number) => void;
  setPageSize: (size: number) => void;
  nextPage: () => void;
  previousPage: () => void;
}

export interface SelectionState<T = Record<string, unknown>> {
  selectedRows: T[];
  toggleRow: (row: T) => void;
  selectAll: (rows: T[]) => void;
  clearSelection: () => void;
}

export interface ExpansionState {
  expandedRows: Set<string>;
  toggleExpand: (rowId: string) => void;
  expandAll: (rowIds: string[]) => void;
  collapseAll: () => void;
}

export interface ColumnManagementState {
  visibleColumns: string[];
  toggleColumn: (field: string) => void;
  columnOrder: string[];
  reorderColumn: (from: number, to: number) => void;
}

export interface PaginationConfig {
  pageSize?: number;
  pageSizeOptions?: number[];
}

export interface UrlSyncConfig {
  enabled?: boolean;
  prefix?: string;
  debounceMs?: number;
}

export interface UseBifrostTableOptions extends UseBifrostOptions {
  query: string;
  columns: ColumnConfig[];
  fields?: string[];
  pagination?: PaginationConfig;
  defaultSort?: SortOption[];
  defaultFilters?: TableFilter;
  multiSort?: boolean;
  rowKey?: string;
  urlSync?: boolean | UrlSyncConfig;
  aggregates?: Record<string, AggregateConfig>;
  expandable?: boolean;
}

export interface UseBifrostTableResult<T = Record<string, unknown>> {
  data: T[];
  columns: ColumnConfig[];
  sorting: SortState;
  filters: FilterState;
  pagination: PaginationState;
  selection: SelectionState<T>;
  aggregates: Record<string, unknown>;
  expansion: ExpansionState;
  columnManagement: ColumnManagementState;
  loading: boolean;
  error: Error | null;
  refetch: () => void;
}

function resolveUrlSyncConfig(urlSync: boolean | UrlSyncConfig | undefined): {
  enabled: boolean;
  prefix: string;
  debounceMs: number;
} {
  if (urlSync === false)
    return { enabled: false, prefix: 'table', debounceMs: 500 };
  if (urlSync === true || urlSync === undefined) {
    return { enabled: true, prefix: 'table', debounceMs: 500 };
  }
  return {
    enabled: urlSync.enabled !== false,
    prefix: urlSync.prefix ?? 'table',
    debounceMs: urlSync.debounceMs ?? 500,
  };
}

function canAccessWindow(): boolean {
  return typeof window !== 'undefined';
}

function computeBuiltinAggregate(fn: AggregateFn, values: number[]): number {
  if (values.length === 0) return 0;
  switch (fn) {
    case 'count':
      return values.length;
    case 'sum':
      return values.reduce((a, b) => a + b, 0);
    case 'avg':
      return values.reduce((a, b) => a + b, 0) / values.length;
    case 'min':
      return Math.min(...values);
    case 'max':
      return Math.max(...values);
  }
}

export function useBifrostTable<T = Record<string, unknown>>(
  options: UseBifrostTableOptions,
): UseBifrostTableResult<T> {
  const {
    query: table,
    columns,
    fields: fieldsProp,
    pagination: paginationConfig,
    defaultSort = [],
    defaultFilters = {},
    multiSort = false,
    rowKey = 'id',
    urlSync,
    aggregates: aggregateConfigs,
    expandable = false,
    ...bifrostOptions
  } = options;

  const syncConfig = resolveUrlSyncConfig(urlSync);
  const initialPageSize = paginationConfig?.pageSize ?? 25;

  const initialUrlState = useMemo(() => {
    if (!syncConfig.enabled || !canAccessWindow()) return null;
    return readFromUrl(syncConfig.prefix);
    // Only read URL on mount
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const [sort, setSort] = useState<SortOption[]>(
    initialUrlState?.sort ?? defaultSort,
  );
  const [filters, setFilters] = useState<TableFilter>(
    initialUrlState?.filter ?? defaultFilters,
  );
  const [page, setPage] = useState(initialUrlState?.page ?? 0);
  const [pageSize, setPageSizeState] = useState(
    initialUrlState?.pageSize ?? initialPageSize,
  );
  const [selectedRows, setSelectedRows] = useState<T[]>([]);
  const [expandedRows, setExpandedRows] = useState<Set<string>>(new Set());
  const [visibleColumns, setVisibleColumns] = useState<string[]>(() =>
    columns.map((c) => c.field),
  );
  const [columnOrder, setColumnOrder] = useState<string[]>(() =>
    columns.map((c) => c.field),
  );

  const debounceTimerRef = useRef<ReturnType<typeof setTimeout>>();

  useEffect(() => {
    if (!syncConfig.enabled || !canAccessWindow()) return;

    if (debounceTimerRef.current) {
      clearTimeout(debounceTimerRef.current);
    }

    debounceTimerRef.current = setTimeout(() => {
      writeToUrl({ sort, page, pageSize, filter: filters }, syncConfig.prefix);
    }, syncConfig.debounceMs);

    return () => {
      if (debounceTimerRef.current) {
        clearTimeout(debounceTimerRef.current);
      }
    };
  }, [
    sort,
    page,
    pageSize,
    filters,
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
      setPage(urlState.page ?? 0);
      if (urlState.pageSize) setPageSizeState(urlState.pageSize);
    };

    window.addEventListener('popstate', handlePopState);
    return () => window.removeEventListener('popstate', handlePopState);
  }, [syncConfig.enabled, syncConfig.prefix, defaultSort, defaultFilters]);

  const computedColumns = useMemo(
    () => columns.filter((c) => c.computed),
    [columns],
  );

  const fields = useMemo(
    () => fieldsProp ?? columns.filter((c) => !c.computed).map((c) => c.field),
    [fieldsProp, columns],
  );

  const queryResult = useBifrostQuery<T[]>(table, {
    fields,
    filter: Object.keys(filters).length > 0 ? filters : undefined,
    sort: sort.length > 0 ? sort : undefined,
    pagination: { limit: pageSize, offset: page * pageSize },
    ...bifrostOptions,
  });

  const dataWithComputed = useMemo(() => {
    const rawData = queryResult.data ?? [];
    if (computedColumns.length === 0) return rawData;
    return rawData.map((row) => {
      const extended = { ...(row as Record<string, unknown>) };
      for (const col of computedColumns) {
        extended[col.field] = col.computed!(extended);
      }
      return extended as T;
    });
  }, [queryResult.data, computedColumns]);

  const computedAggregates = useMemo(() => {
    if (!aggregateConfigs) return {};
    const result: Record<string, unknown> = {};
    for (const [key, config] of Object.entries(aggregateConfigs)) {
      if (typeof config.fn === 'function') {
        const values = dataWithComputed.map((row) =>
          config.field ? (row as Record<string, unknown>)[config.field] : row,
        );
        result[key] = config.fn(values);
      } else {
        const field = config.field;
        const values: number[] = [];
        if (config.fn === 'count') {
          result[key] = dataWithComputed.length;
          continue;
        }
        for (const row of dataWithComputed) {
          if (field) {
            const val = (row as Record<string, unknown>)[field];
            if (typeof val === 'number') values.push(val);
          }
        }
        result[key] = computeBuiltinAggregate(config.fn, values);
      }
    }
    return result;
  }, [dataWithComputed, aggregateConfigs]);

  const toggleExpand = useCallback(
    (rowId: string) => {
      if (!expandable) return;
      setExpandedRows((prev) => {
        const next = new Set(prev);
        if (next.has(rowId)) {
          next.delete(rowId);
        } else {
          next.add(rowId);
        }
        return next;
      });
    },
    [expandable],
  );

  const expandAll = useCallback(
    (rowIds: string[]) => {
      if (!expandable) return;
      setExpandedRows(new Set(rowIds));
    },
    [expandable],
  );

  const collapseAll = useCallback(() => {
    setExpandedRows(new Set());
  }, []);

  const toggleColumn = useCallback((field: string) => {
    setVisibleColumns((prev) =>
      prev.includes(field) ? prev.filter((f) => f !== field) : [...prev, field],
    );
  }, []);

  const reorderColumn = useCallback((from: number, to: number) => {
    setColumnOrder((prev) => {
      if (from < 0 || from >= prev.length || to < 0 || to >= prev.length) {
        return prev;
      }
      const next = [...prev];
      const [moved] = next.splice(from, 1);
      next.splice(to, 0, moved);
      return next;
    });
  }, []);

  const toggleSort = useCallback(
    (field: string) => {
      const col = columns.find((c) => c.field === field);
      if (!col?.sortable) return;

      setSort((prev) => {
        const existing = prev.find((s) => s.field === field);

        if (!existing) {
          const newSort: SortOption = { field, direction: 'asc' };
          return multiSort ? [...prev, newSort] : [newSort];
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
    setPage(0);
  }, []);

  const handleSetFilters = useCallback((newFilters: TableFilter) => {
    setFilters(newFilters);
    setPage(0);
  }, []);

  const handleSetSorting = useCallback((newSort: SortOption[]) => {
    setSort(newSort);
    setPage(0);
  }, []);

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

  const toggleRow = useCallback(
    (row: T) => {
      setSelectedRows((prev) => {
        const key = (row as Record<string, unknown>)[rowKey];
        const exists = prev.some(
          (r) => (r as Record<string, unknown>)[rowKey] === key,
        );
        if (exists) {
          return prev.filter(
            (r) => (r as Record<string, unknown>)[rowKey] !== key,
          );
        }
        return [...prev, row];
      });
    },
    [rowKey],
  );

  const selectAll = useCallback((rows: T[]) => {
    setSelectedRows(rows);
  }, []);

  const clearSelection = useCallback(() => {
    setSelectedRows([]);
  }, []);

  return {
    data: dataWithComputed,
    columns,
    sorting: {
      current: sort,
      setSorting: handleSetSorting,
      toggleSort,
    },
    filters: {
      current: filters,
      setFilters: handleSetFilters,
      setColumnFilter,
      clearFilters,
    },
    pagination: {
      page,
      pageSize,
      setPage: handleSetPage,
      setPageSize: handleSetPageSize,
      nextPage,
      previousPage,
    },
    selection: {
      selectedRows,
      toggleRow,
      selectAll,
      clearSelection,
    },
    aggregates: computedAggregates,
    expansion: {
      expandedRows,
      toggleExpand,
      expandAll,
      collapseAll,
    },
    columnManagement: {
      visibleColumns,
      toggleColumn,
      columnOrder,
      reorderColumn,
    },
    loading: queryResult.isLoading,
    error: queryResult.error,
    refetch: queryResult.refetch,
  };
}
