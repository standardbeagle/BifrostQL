import { useState, useCallback, useMemo, useEffect, useRef } from 'react';
import { useBifrostQuery } from './use-bifrost-query';
import { readFromUrl, writeToUrl } from '../utils/url-state';
import type { UseBifrostOptions } from './use-bifrost';
import type {
  SortOption,
  TableFilter,
  FieldFilter,
  AdvancedFilter,
  CompoundFilter,
} from '../types';

export type AggregateFn = 'sum' | 'avg' | 'min' | 'max' | 'count';

export type SortDirection = 'asc' | 'desc';

export type CustomSortFn = (
  a: unknown,
  b: unknown,
  direction: SortDirection,
) => number;

export interface AggregateConfig {
  field?: string;
  fn: AggregateFn | ((values: unknown[]) => unknown);
}

export type EditorType =
  | 'text'
  | 'number'
  | 'select'
  | 'date'
  | 'checkbox'
  | 'textarea';

export type CellValidator = (
  value: unknown,
  row: Record<string, unknown>,
) => string | null | Promise<string | null>;

export interface ColumnConfig {
  field: string;
  header: string;
  width?: number;
  sortable?: boolean;
  filterable?: boolean;
  filterType?: 'text' | 'number' | 'select' | 'date';
  filterOptions?: Array<{ label: string; value: string | number }>;
  computed?: (row: Record<string, unknown>) => unknown;
  customSort?: CustomSortFn;
  editable?: boolean;
  readOnly?: boolean;
  editorType?: EditorType;
  editorOptions?: Array<{ label: string; value: string | number }>;
  validate?: CellValidator;
  editorComponent?: (props: CellEditorProps) => unknown;
}

export interface CellEditorProps {
  value: unknown;
  onChange: (value: unknown) => void;
  onCommit: () => void;
  onCancel: () => void;
  column: ColumnConfig;
  row: Record<string, unknown>;
}

export interface LocalStorageConfig {
  key: string;
  persistFilters?: boolean;
}

export interface FilterPreset {
  name: string;
  filters: TableFilter;
  compoundFilter?: CompoundFilter;
}

export interface SortState {
  current: SortOption[];
  setSorting: (sort: SortOption[]) => void;
  toggleSort: (field: string, multi?: boolean) => void;
  addSort: (field: string, direction: SortDirection) => void;
  removeSort: (field: string) => void;
  clearSort: () => void;
  getSortIndicator: (field: string) => string;
  getSortPriority: (field: string) => number;
}

export interface FilterState {
  current: TableFilter;
  compoundFilter: CompoundFilter | null;
  activeFilterCount: number;
  setFilters: (filters: TableFilter) => void;
  setColumnFilter: (field: string, value: TableFilter[string]) => void;
  setCompoundFilter: (filter: CompoundFilter | null) => void;
  clearFilters: () => void;
  presets: FilterPreset[];
  savePreset: (name: string) => void;
  loadPreset: (name: string) => void;
  deletePreset: (name: string) => void;
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

export interface CellError {
  field: string;
  message: string;
}

export interface RowEditState {
  original: Record<string, unknown>;
  changes: Record<string, unknown>;
  errors: Map<string, string>;
  saving: boolean;
}

export interface EditingState {
  editingCell: { rowKey: string; field: string } | null;
  dirtyRows: Map<string, RowEditState>;
  isDirty: boolean;
  dirtyRowCount: number;
  startEditing: (rowKey: string, field: string) => void;
  cancelEditing: () => void;
  setCellValue: (rowKey: string, field: string, value: unknown) => void;
  commitCell: () => Promise<void>;
  getCellValue: (rowKey: string, field: string) => unknown;
  isCellDirty: (rowKey: string, field: string) => boolean;
  getCellError: (rowKey: string, field: string) => string | null;
  isRowDirty: (rowKey: string) => boolean;
  getRowChanges: (rowKey: string) => Record<string, unknown>;
  saveRow: (rowKey: string) => Promise<boolean>;
  saveAllDirty: () => Promise<{ saved: number; failed: number }>;
  discardRow: (rowKey: string) => void;
  discardAll: () => void;
  isColumnEditable: (field: string) => boolean;
}

export type RowUpdateFn = (
  row: Record<string, unknown>,
  changes: Record<string, unknown>,
) => Promise<void>;

export type BatchSaveFn = (
  rows: Array<{
    row: Record<string, unknown>;
    changes: Record<string, unknown>;
  }>,
) => Promise<void>;

export interface PaginationConfig {
  pageSize?: number;
  pageSizeOptions?: number[];
}

export interface UrlSyncConfig {
  enabled?: boolean;
  prefix?: string;
  debounceMs?: number;
}

export interface ClientSideSortConfig {
  enabled: boolean;
  threshold?: number;
}

export interface ClientSideFilterConfig {
  enabled: boolean;
  threshold?: number;
}

export interface UseBifrostTableOptions extends UseBifrostOptions {
  query: string;
  columns: ColumnConfig[];
  fields?: string[];
  pagination?: PaginationConfig;
  defaultSort?: SortOption[];
  defaultFilters?: TableFilter;
  multiSort?: boolean;
  clientSideSort?: boolean | ClientSideSortConfig;
  clientSideFilter?: boolean | ClientSideFilterConfig;
  filterDebounceMs?: number;
  rowKey?: string;
  urlSync?: boolean | UrlSyncConfig;
  localStorage?: LocalStorageConfig;
  aggregates?: Record<string, AggregateConfig>;
  expandable?: boolean;
  editable?: boolean;
  autoSave?: boolean;
  onRowUpdate?: RowUpdateFn;
  onBatchSave?: BatchSaveFn;
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
  editing: EditingState;
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

function resolveClientSideSortConfig(
  config: boolean | ClientSideSortConfig | undefined,
): { enabled: boolean; threshold: number } {
  if (config === true) return { enabled: true, threshold: Infinity };
  if (config === false || config === undefined)
    return { enabled: false, threshold: 0 };
  return {
    enabled: config.enabled,
    threshold: config.threshold ?? Infinity,
  };
}

function resolveClientSideFilterConfig(
  config: boolean | ClientSideFilterConfig | undefined,
): { enabled: boolean; threshold: number } {
  if (config === true) return { enabled: true, threshold: Infinity };
  if (config === false || config === undefined)
    return { enabled: false, threshold: 0 };
  return {
    enabled: config.enabled,
    threshold: config.threshold ?? Infinity,
  };
}

function readSortFromLocalStorage(key: string): SortOption[] | null {
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

function writeSortToLocalStorage(key: string, sort: SortOption[]): void {
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

function readFiltersFromLocalStorage(key: string): TableFilter | null {
  if (typeof window === 'undefined') return null;
  try {
    const raw = window.localStorage.getItem(`${key}_filters`);
    if (!raw) return null;
    const parsed = JSON.parse(raw);
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed))
      return null;
    return parsed as TableFilter;
  } catch {
    return null;
  }
}

function writeFiltersToLocalStorage(key: string, filters: TableFilter): void {
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

function readPresetsFromLocalStorage(key: string): FilterPreset[] {
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

function writePresetsToLocalStorage(
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

function countActiveFilters(
  filters: TableFilter,
  compoundFilter: CompoundFilter | null,
): number {
  let count = Object.keys(filters).length;
  if (compoundFilter) {
    if (compoundFilter._and) count += compoundFilter._and.length;
    if (compoundFilter._or) count += compoundFilter._or.length;
  }
  return count;
}

function defaultCompare(a: unknown, b: unknown): number {
  if (a === b) return 0;
  if (a === null || a === undefined) return 1;
  if (b === null || b === undefined) return -1;
  if (typeof a === 'number' && typeof b === 'number') return a - b;
  return String(a).localeCompare(String(b));
}

function clientSortRows<T>(
  rows: T[],
  sort: SortOption[],
  columns: ColumnConfig[],
): T[] {
  if (sort.length === 0) return rows;
  const sorted = [...rows];
  const columnMap = new Map(columns.map((c) => [c.field, c]));

  sorted.sort((a, b) => {
    for (const s of sort) {
      const col = columnMap.get(s.field);
      const aVal = (a as Record<string, unknown>)[s.field];
      const bVal = (b as Record<string, unknown>)[s.field];
      let cmp: number;
      if (col?.customSort) {
        cmp = col.customSort(aVal, bVal, s.direction);
      } else {
        cmp = defaultCompare(aVal, bVal);
        if (s.direction === 'desc') cmp = -cmp;
      }
      if (cmp !== 0) return cmp;
    }
    return 0;
  });
  return sorted;
}

function matchesFieldFilter(value: unknown, filter: FieldFilter): boolean {
  for (const [op, expected] of Object.entries(filter)) {
    switch (op) {
      case '_eq':
        if (value !== expected) return false;
        break;
      case '_neq':
        if (value === expected) return false;
        break;
      case '_gt':
        if (value === null || value === undefined) return false;
        if ((value as number) <= (expected as number)) return false;
        break;
      case '_gte':
        if (value === null || value === undefined) return false;
        if ((value as number) < (expected as number)) return false;
        break;
      case '_lt':
        if (value === null || value === undefined) return false;
        if ((value as number) >= (expected as number)) return false;
        break;
      case '_lte':
        if (value === null || value === undefined) return false;
        if ((value as number) > (expected as number)) return false;
        break;
      case '_in':
        if (
          !(expected as Array<string | number>).includes(
            value as string | number,
          )
        )
          return false;
        break;
      case '_nin':
        if (
          (expected as Array<string | number>).includes(
            value as string | number,
          )
        )
          return false;
        break;
      case '_contains':
        if (
          typeof value !== 'string' ||
          !value.toLowerCase().includes((expected as string).toLowerCase())
        )
          return false;
        break;
      case '_ncontains':
        if (
          typeof value !== 'string' ||
          value.toLowerCase().includes((expected as string).toLowerCase())
        )
          return false;
        break;
      case '_starts_with':
        if (
          typeof value !== 'string' ||
          !value.toLowerCase().startsWith((expected as string).toLowerCase())
        )
          return false;
        break;
      case '_ends_with':
        if (
          typeof value !== 'string' ||
          !value.toLowerCase().endsWith((expected as string).toLowerCase())
        )
          return false;
        break;
      case '_between':
        if (value === null || value === undefined) return false;
        if (Array.isArray(expected) && expected.length === 2) {
          if ((value as number) < (expected[0] as number)) return false;
          if ((value as number) > (expected[1] as number)) return false;
        }
        break;
      case '_null':
        if (expected === true && value !== null && value !== undefined)
          return false;
        if (expected === false && (value === null || value === undefined))
          return false;
        break;
      case '_nnull':
        if (expected === true && (value === null || value === undefined))
          return false;
        if (expected === false && value !== null && value !== undefined)
          return false;
        break;
    }
  }
  return true;
}

function matchesTableFilter(
  row: Record<string, unknown>,
  filter: TableFilter,
): boolean {
  for (const [field, value] of Object.entries(filter)) {
    const rowVal = row[field];
    if (value === null) {
      if (rowVal !== null && rowVal !== undefined) return false;
    } else if (typeof value !== 'object') {
      if (rowVal !== value) return false;
    } else {
      if (!matchesFieldFilter(rowVal, value as FieldFilter)) return false;
    }
  }
  return true;
}

function matchesAdvancedFilter(
  row: Record<string, unknown>,
  filter: AdvancedFilter,
): boolean {
  if ('_and' in filter || '_or' in filter) {
    const compound = filter as CompoundFilter;
    if (compound._and) {
      if (!compound._and.every((f) => matchesAdvancedFilter(row, f)))
        return false;
    }
    if (compound._or) {
      if (!compound._or.some((f) => matchesAdvancedFilter(row, f)))
        return false;
    }
    return true;
  }
  return matchesTableFilter(row, filter as TableFilter);
}

function clientFilterRows<T>(
  rows: T[],
  filters: TableFilter,
  compoundFilter: CompoundFilter | null,
): T[] {
  let result = rows;
  if (Object.keys(filters).length > 0) {
    result = result.filter((row) =>
      matchesTableFilter(row as Record<string, unknown>, filters),
    );
  }
  if (compoundFilter) {
    result = result.filter((row) =>
      matchesAdvancedFilter(row as Record<string, unknown>, compoundFilter),
    );
  }
  return result;
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

function mergeFiltersForQuery(
  filters: TableFilter,
  compoundFilter: CompoundFilter | null,
): AdvancedFilter | undefined {
  const hasFilters = Object.keys(filters).length > 0;
  const hasCompound = compoundFilter !== null;

  if (!hasFilters && !hasCompound) return undefined;
  if (hasFilters && !hasCompound) return filters;
  if (!hasFilters && hasCompound) return compoundFilter;

  // Merge: wrap column filters + compound filter into a single _and
  const parts: Array<TableFilter | CompoundFilter> = [filters];
  parts.push(compoundFilter!);
  return { _and: parts };
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
    clientSideSort: clientSideSortProp,
    clientSideFilter: clientSideFilterProp,
    filterDebounceMs = 300,
    rowKey = 'id',
    urlSync,
    localStorage: localStorageConfig,
    aggregates: aggregateConfigs,
    expandable = false,
    editable = false,
    autoSave = false,
    onRowUpdate,
    onBatchSave,
    ...bifrostOptions
  } = options;

  const syncConfig = resolveUrlSyncConfig(urlSync);
  const clientSortConfig = resolveClientSideSortConfig(clientSideSortProp);
  const clientFilterConfig =
    resolveClientSideFilterConfig(clientSideFilterProp);
  const initialPageSize = paginationConfig?.pageSize ?? 25;

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
  const [selectedRows, setSelectedRows] = useState<T[]>([]);
  const [expandedRows, setExpandedRows] = useState<Set<string>>(new Set());
  const [visibleColumns, setVisibleColumns] = useState<string[]>(() =>
    columns.map((c) => c.field),
  );
  const [columnOrder, setColumnOrder] = useState<string[]>(() =>
    columns.map((c) => c.field),
  );

  const [editingCell, setEditingCell] = useState<{
    rowKey: string;
    field: string;
  } | null>(null);
  const [dirtyRows, setDirtyRows] = useState<Map<string, RowEditState>>(
    () => new Map(),
  );
  const optimisticRollbackRef = useRef<Map<string, Record<string, unknown>>>(
    new Map(),
  );

  const urlDebounceTimerRef = useRef<ReturnType<typeof setTimeout>>();
  const filterDebounceTimerRef = useRef<ReturnType<typeof setTimeout>>();

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

  const computedColumns = useMemo(
    () => columns.filter((c) => c.computed),
    [columns],
  );

  const fields = useMemo(
    () => fieldsProp ?? columns.filter((c) => !c.computed).map((c) => c.field),
    [fieldsProp, columns],
  );

  const serverSort =
    !clientSortConfig.enabled && sort.length > 0 ? sort : undefined;

  const serverFilter = useMemo(() => {
    if (clientFilterConfig.enabled) return undefined;
    return mergeFiltersForQuery(debouncedFilters, compoundFilter);
  }, [debouncedFilters, compoundFilter, clientFilterConfig.enabled]);

  const queryResult = useBifrostQuery<T[]>(table, {
    fields,
    filter: serverFilter,
    sort: serverSort,
    pagination: { limit: pageSize, offset: page * pageSize },
    ...bifrostOptions,
  });

  const shouldClientSort =
    clientSortConfig.enabled &&
    (queryResult.data ?? []).length <= clientSortConfig.threshold;

  const shouldClientFilter =
    clientFilterConfig.enabled &&
    (queryResult.data ?? []).length <= clientFilterConfig.threshold;

  const dataWithComputed = useMemo(() => {
    const rawData = queryResult.data ?? [];
    let processed = rawData;
    if (computedColumns.length > 0) {
      processed = rawData.map((row) => {
        const extended = { ...(row as Record<string, unknown>) };
        for (const col of computedColumns) {
          extended[col.field] = col.computed!(extended);
        }
        return extended as T;
      });
    }
    if (shouldClientFilter) {
      processed = clientFilterRows(processed, debouncedFilters, compoundFilter);
    }
    if (shouldClientSort && sort.length > 0) {
      processed = clientSortRows(processed, sort, columns);
    }
    return processed;
  }, [
    queryResult.data,
    computedColumns,
    shouldClientFilter,
    debouncedFilters,
    compoundFilter,
    shouldClientSort,
    sort,
    columns,
  ]);

  const activeFilterCount = useMemo(
    () => countActiveFilters(filters, compoundFilter),
    [filters, compoundFilter],
  );

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
      return entry.direction === 'asc' ? '\u25B2' : '\u25BC';
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

  const editableColumnSet = useMemo(() => {
    const set = new Set<string>();
    for (const col of columns) {
      if (col.readOnly) continue;
      if (col.computed) continue;
      if (editable && col.editable !== false) {
        set.add(col.field);
      } else if (col.editable) {
        set.add(col.field);
      }
    }
    return set;
  }, [columns, editable]);

  const isColumnEditable = useCallback(
    (field: string) => editableColumnSet.has(field),
    [editableColumnSet],
  );

  const getRowByKey = useCallback(
    (key: string): Record<string, unknown> | undefined => {
      return dataWithComputed.find(
        (row) => String((row as Record<string, unknown>)[rowKey]) === key,
      ) as Record<string, unknown> | undefined;
    },
    [dataWithComputed, rowKey],
  );

  const startEditing = useCallback(
    (rk: string, field: string) => {
      if (!editableColumnSet.has(field)) return;
      setEditingCell({ rowKey: rk, field });
      setDirtyRows((prev) => {
        if (prev.has(rk)) return prev;
        const row = getRowByKey(rk);
        if (!row) return prev;
        const next = new Map(prev);
        next.set(rk, {
          original: { ...row },
          changes: {},
          errors: new Map(),
          saving: false,
        });
        return next;
      });
    },
    [editableColumnSet, getRowByKey],
  );

  const cancelEditing = useCallback(() => {
    setEditingCell(null);
  }, []);

  const setCellValue = useCallback(
    (rk: string, field: string, value: unknown) => {
      if (!editableColumnSet.has(field)) return;
      setDirtyRows((prev) => {
        const existing = prev.get(rk);
        const row = existing?.original ?? getRowByKey(rk);
        if (!row) return prev;

        const next = new Map(prev);
        const editState = existing ?? {
          original: { ...row },
          changes: {},
          errors: new Map(),
          saving: false,
        };

        const originalValue = editState.original[field];
        const newChanges = { ...editState.changes };

        if (originalValue === value) {
          delete newChanges[field];
        } else {
          newChanges[field] = value;
        }

        if (Object.keys(newChanges).length === 0) {
          next.delete(rk);
        } else {
          next.set(rk, {
            ...editState,
            changes: newChanges,
            errors: new Map(editState.errors),
          });
        }
        return next;
      });
    },
    [editableColumnSet, getRowByKey],
  );

  const validateCell = useCallback(
    async (
      field: string,
      value: unknown,
      row: Record<string, unknown>,
    ): Promise<string | null> => {
      const col = columns.find((c) => c.field === field);
      if (!col?.validate) return null;
      return col.validate(value, row);
    },
    [columns],
  );

  const commitCell = useCallback(async () => {
    if (!editingCell) return;
    const { rowKey: rk, field } = editingCell;
    const editState = dirtyRows.get(rk);
    if (!editState) {
      setEditingCell(null);
      return;
    }

    const value =
      field in editState.changes
        ? editState.changes[field]
        : editState.original[field];
    const mergedRow = { ...editState.original, ...editState.changes };
    const error = await validateCell(field, value, mergedRow);

    if (error) {
      setDirtyRows((prev) => {
        const state = prev.get(rk);
        if (!state) return prev;
        const next = new Map(prev);
        const errors = new Map(state.errors);
        errors.set(field, error);
        next.set(rk, { ...state, errors });
        return next;
      });
      return;
    }

    setDirtyRows((prev) => {
      const state = prev.get(rk);
      if (!state) return prev;
      const next = new Map(prev);
      const errors = new Map(state.errors);
      errors.delete(field);
      next.set(rk, { ...state, errors });
      return next;
    });

    setEditingCell(null);

    if (autoSave && onRowUpdate && editState.changes[field] !== undefined) {
      const changes = { [field]: editState.changes[field] };
      try {
        await onRowUpdate(editState.original, changes);
        setDirtyRows((prev) => {
          const state = prev.get(rk);
          if (!state) return prev;
          const next = new Map(prev);
          const remaining = { ...state.changes };
          delete remaining[field];
          if (Object.keys(remaining).length === 0) {
            next.delete(rk);
          } else {
            next.set(rk, { ...state, changes: remaining });
          }
          return next;
        });
        queryResult.refetch();
      } catch {
        // Auto-save failed; keep dirty state for retry
      }
    }
  }, [
    editingCell,
    dirtyRows,
    validateCell,
    autoSave,
    onRowUpdate,
    queryResult,
  ]);

  const getCellValue = useCallback(
    (rk: string, field: string): unknown => {
      const editState = dirtyRows.get(rk);
      if (editState && field in editState.changes) {
        return editState.changes[field];
      }
      const row = getRowByKey(rk);
      return row ? row[field] : undefined;
    },
    [dirtyRows, getRowByKey],
  );

  const isCellDirty = useCallback(
    (rk: string, field: string): boolean => {
      const editState = dirtyRows.get(rk);
      return editState ? field in editState.changes : false;
    },
    [dirtyRows],
  );

  const getCellError = useCallback(
    (rk: string, field: string): string | null => {
      const editState = dirtyRows.get(rk);
      return editState?.errors.get(field) ?? null;
    },
    [dirtyRows],
  );

  const isRowDirty = useCallback(
    (rk: string): boolean => dirtyRows.has(rk),
    [dirtyRows],
  );

  const getRowChanges = useCallback(
    (rk: string): Record<string, unknown> => dirtyRows.get(rk)?.changes ?? {},
    [dirtyRows],
  );

  const saveRow = useCallback(
    async (rk: string): Promise<boolean> => {
      const editState = dirtyRows.get(rk);
      if (!editState || Object.keys(editState.changes).length === 0)
        return true;

      const mergedRow = { ...editState.original, ...editState.changes };
      const errors = new Map<string, string>();
      for (const [field, value] of Object.entries(editState.changes)) {
        const error = await validateCell(field, value, mergedRow);
        if (error) errors.set(field, error);
      }

      if (errors.size > 0) {
        setDirtyRows((prev) => {
          const state = prev.get(rk);
          if (!state) return prev;
          const next = new Map(prev);
          next.set(rk, { ...state, errors });
          return next;
        });
        return false;
      }

      if (!onRowUpdate) return false;

      setDirtyRows((prev) => {
        const state = prev.get(rk);
        if (!state) return prev;
        const next = new Map(prev);
        next.set(rk, { ...state, saving: true });
        return next;
      });

      optimisticRollbackRef.current.set(rk, editState.original);

      try {
        await onRowUpdate(editState.original, editState.changes);
        setDirtyRows((prev) => {
          const next = new Map(prev);
          next.delete(rk);
          return next;
        });
        optimisticRollbackRef.current.delete(rk);
        queryResult.refetch();
        return true;
      } catch {
        setDirtyRows((prev) => {
          const state = prev.get(rk);
          if (!state) return prev;
          const next = new Map(prev);
          next.set(rk, { ...state, saving: false });
          return next;
        });
        optimisticRollbackRef.current.delete(rk);
        return false;
      }
    },
    [dirtyRows, validateCell, onRowUpdate, queryResult],
  );

  const saveAllDirty = useCallback(async (): Promise<{
    saved: number;
    failed: number;
  }> => {
    const dirtyEntries = Array.from(dirtyRows.entries()).filter(
      ([, state]) => Object.keys(state.changes).length > 0,
    );
    if (dirtyEntries.length === 0) return { saved: 0, failed: 0 };

    if (onBatchSave) {
      const batch = dirtyEntries.map(([, state]) => ({
        row: state.original,
        changes: state.changes,
      }));

      const errorMap = new Map<string, Map<string, string>>();
      for (const [rk, state] of dirtyEntries) {
        const mergedRow = { ...state.original, ...state.changes };
        const rowErrors = new Map<string, string>();
        for (const [field, value] of Object.entries(state.changes)) {
          const error = await validateCell(field, value, mergedRow);
          if (error) rowErrors.set(field, error);
        }
        if (rowErrors.size > 0) errorMap.set(rk, rowErrors);
      }

      if (errorMap.size > 0) {
        setDirtyRows((prev) => {
          const next = new Map(prev);
          for (const [rk, errors] of errorMap) {
            const state = next.get(rk);
            if (state) next.set(rk, { ...state, errors });
          }
          return next;
        });
        return { saved: 0, failed: dirtyEntries.length };
      }

      try {
        await onBatchSave(batch);
        setDirtyRows(new Map());
        queryResult.refetch();
        return { saved: dirtyEntries.length, failed: 0 };
      } catch {
        return { saved: 0, failed: dirtyEntries.length };
      }
    }

    let saved = 0;
    let failed = 0;
    for (const [rk] of dirtyEntries) {
      const success = await saveRow(rk);
      if (success) saved++;
      else failed++;
    }
    return { saved, failed };
  }, [dirtyRows, onBatchSave, validateCell, saveRow, queryResult]);

  const discardRow = useCallback((rk: string) => {
    setDirtyRows((prev) => {
      const next = new Map(prev);
      next.delete(rk);
      return next;
    });
    setEditingCell((prev) => (prev?.rowKey === rk ? null : prev));
  }, []);

  const discardAll = useCallback(() => {
    setDirtyRows(new Map());
    setEditingCell(null);
  }, []);

  const isDirty = dirtyRows.size > 0;
  const dirtyRowCount = dirtyRows.size;

  return {
    data: dataWithComputed,
    columns,
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
    filters: {
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
    editing: {
      editingCell,
      dirtyRows,
      isDirty,
      dirtyRowCount,
      startEditing,
      cancelEditing,
      setCellValue,
      commitCell,
      getCellValue,
      isCellDirty,
      getCellError,
      isRowDirty,
      getRowChanges,
      saveRow,
      saveAllDirty,
      discardRow,
      discardAll,
      isColumnEditable,
    },
    loading: queryResult.isLoading,
    error: queryResult.error,
    refetch: queryResult.refetch,
  };
}
