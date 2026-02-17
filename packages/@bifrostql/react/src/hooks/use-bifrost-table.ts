import { useState, useCallback, useMemo, useEffect, useRef, useContext } from 'react';
import { useBifrostQuery } from './use-bifrost-query';
import { BifrostContext } from '../components/bifrost-provider';
import { executeGraphQL } from '../utils/graphql-client';
import { buildGraphqlQuery } from '../utils/query-builder';
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

export type AggregateFormat = 'currency' | 'percentage' | 'number';

export type SortDirection = 'asc' | 'desc';

export type CustomSortFn = (
  a: unknown,
  b: unknown,
  direction: SortDirection,
) => number;

export interface AggregateConfig {
  field?: string;
  fn: AggregateFn | ((values: unknown[]) => unknown);
  format?: AggregateFormat | ((value: unknown) => string);
}

export interface GroupByConfig {
  field: string;
  aggregates: Record<string, AggregateConfig>;
}

export interface AggregateResult {
  value: unknown;
  formatted: string | null;
}

export interface GroupRow {
  groupKey: unknown;
  rows: Record<string, unknown>[];
  aggregates: Record<string, AggregateResult>;
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

export interface ChildQueryConfig {
  query: string;
  parentKeyField?: string;
  childFilterField?: string;
  fields?: string[];
}

export interface ChildRowData {
  data: unknown[] | null;
  loading: boolean;
  error: Error | null;
}

export interface ExpansionState {
  expandedRows: Set<string>;
  toggleExpand: (rowId: string) => void;
  expandAll: (rowIds: string[]) => void;
  collapseAll: () => void;
  getChildData: (rowId: string) => ChildRowData;
  fetchChildData: (rowId: string, parentRow: Record<string, unknown>) => void;
  isChildLoading: (rowId: string) => boolean;
  childError: (rowId: string) => Error | null;
  clearChildCache: (rowId?: string) => void;
}

export type PinPosition = 'left' | 'right' | null;

export interface ColumnPreset {
  name: string;
  visibleColumns: string[];
  columnOrder: string[];
  columnWidths: Record<string, number>;
  pinnedColumns: Record<string, PinPosition>;
}

export interface ColumnManagementConfig {
  resizable?: boolean;
  reorderable?: boolean;
  hideable?: boolean;
  freezable?: boolean;
}

export type ExportFormat = 'csv' | 'excel' | 'json';

export type ExportFormatter = (
  value: unknown,
  field: string,
  row: Record<string, unknown>,
) => string;

export interface ExportConfig {
  formats?: ExportFormat[];
  filename?: string;
  formatters?: Record<string, ExportFormatter>;
}

export interface ColumnManagementState {
  visibleColumns: string[];
  toggleColumn: (field: string) => void;
  showColumn: (field: string) => void;
  hideColumn: (field: string) => void;
  showAllColumns: () => void;
  columnOrder: string[];
  reorderColumn: (from: number, to: number) => void;
  columnWidths: Record<string, number>;
  resizeColumn: (field: string, width: number) => void;
  autoFitColumn: (field: string) => void;
  autoFitAllColumns: () => void;
  pinnedColumns: Record<string, PinPosition>;
  pinColumn: (field: string, position: PinPosition) => void;
  unpinColumn: (field: string) => void;
  presets: ColumnPreset[];
  savePreset: (name: string) => void;
  loadPreset: (name: string) => void;
  deletePreset: (name: string) => void;
  resetColumns: () => void;
}

export interface ExportState {
  exportCsv: (allPages?: boolean) => void;
  exportExcel: (allPages?: boolean) => void;
  exportJson: (allPages?: boolean) => void;
  copyToClipboard: (allPages?: boolean) => Promise<void>;
  downloadFile: (
    format: ExportFormat,
    allPages?: boolean,
  ) => void;
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
  groupBy?: GroupByConfig;
  expandable?: boolean;
  childQuery?: ChildQueryConfig;
  editable?: boolean;
  autoSave?: boolean;
  onRowUpdate?: RowUpdateFn;
  onBatchSave?: BatchSaveFn;
  columnManagement?: ColumnManagementConfig;
  export?: ExportConfig;
}

export interface UseBifrostTableResult<T = Record<string, unknown>> {
  data: T[];
  columns: ColumnConfig[];
  sorting: SortState;
  filters: FilterState;
  pagination: PaginationState;
  selection: SelectionState<T>;
  aggregates: Record<string, unknown>;
  formattedAggregates: Record<string, AggregateResult>;
  groups: GroupRow[];
  expansion: ExpansionState;
  columnManagement: ColumnManagementState;
  editing: EditingState;
  export: ExportState;
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

function readColumnPresetsFromLocalStorage(key: string): ColumnPreset[] {
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

function writeColumnPresetsToLocalStorage(
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

function escapeCsvValue(value: unknown): string {
  if (value === null || value === undefined) return '';
  const str = String(value);
  if (str.includes(',') || str.includes('"') || str.includes('\n') || str.includes('\r')) {
    return `"${str.replace(/"/g, '""')}"`;
  }
  return str;
}

function formatExportValue(
  value: unknown,
  field: string,
  row: Record<string, unknown>,
  formatters?: Record<string, ExportFormatter>,
): string {
  if (formatters?.[field]) {
    return formatters[field](value, field, row);
  }
  if (value === null || value === undefined) return '';
  return String(value);
}

function rowsToCsv(
  rows: Record<string, unknown>[],
  fields: string[],
  headers: string[],
  formatters?: Record<string, ExportFormatter>,
): string {
  const headerLine = headers.map(escapeCsvValue).join(',');
  const dataLines = rows.map((row) =>
    fields
      .map((field) =>
        escapeCsvValue(formatExportValue(row[field], field, row, formatters)),
      )
      .join(','),
  );
  return [headerLine, ...dataLines].join('\n');
}

function rowsToTsv(
  rows: Record<string, unknown>[],
  fields: string[],
  headers: string[],
  formatters?: Record<string, ExportFormatter>,
): string {
  const headerLine = headers.join('\t');
  const dataLines = rows.map((row) =>
    fields
      .map((field) =>
        formatExportValue(row[field], field, row, formatters).replace(/\t/g, ' '),
      )
      .join('\t'),
  );
  return [headerLine, ...dataLines].join('\n');
}

function rowsToJson(
  rows: Record<string, unknown>[],
  fields: string[],
  formatters?: Record<string, ExportFormatter>,
): string {
  const filtered = rows.map((row) => {
    const obj: Record<string, unknown> = {};
    for (const field of fields) {
      if (formatters?.[field]) {
        obj[field] = formatters[field](row[field], field, row);
      } else {
        obj[field] = row[field];
      }
    }
    return obj;
  });
  return JSON.stringify(filtered, null, 2);
}

function triggerDownload(content: string, filename: string, mimeType: string): void {
  if (typeof window === 'undefined' || typeof document === 'undefined') return;
  const blob = new Blob([content], { type: mimeType });
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = filename;
  document.body.appendChild(link);
  link.click();
  document.body.removeChild(link);
  URL.revokeObjectURL(url);
}

const DEFAULT_COLUMN_WIDTH = 150;

function estimateColumnWidth(
  field: string,
  header: string,
  rows: Record<string, unknown>[],
): number {
  const headerLen = header.length;
  let maxLen = headerLen;
  const sampleSize = Math.min(rows.length, 100);
  for (let i = 0; i < sampleSize; i++) {
    const val = rows[i][field];
    const len = val === null || val === undefined ? 0 : String(val).length;
    if (len > maxLen) maxLen = len;
  }
  return Math.max(50, Math.min(maxLen * 9 + 16, 500));
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

function formatAggregateValue(
  value: unknown,
  format: AggregateFormat | ((value: unknown) => string) | undefined,
): string | null {
  if (!format) return null;
  if (typeof format === 'function') return format(value);
  if (typeof value !== 'number') return String(value ?? '');
  switch (format) {
    case 'currency':
      return value.toLocaleString(undefined, {
        style: 'currency',
        currency: 'USD',
      });
    case 'percentage':
      return value.toLocaleString(undefined, {
        style: 'percent',
        minimumFractionDigits: 1,
        maximumFractionDigits: 1,
      });
    case 'number':
      return value.toLocaleString();
  }
}

function computeAggregateForRows(
  rows: Record<string, unknown>[],
  config: AggregateConfig,
): unknown {
  if (typeof config.fn === 'function') {
    const values = rows.map((row) =>
      config.field ? row[config.field] : row,
    );
    return config.fn(values);
  }
  if (config.fn === 'count') return rows.length;
  const values: number[] = [];
  if (config.field) {
    for (const row of rows) {
      const val = row[config.field];
      if (typeof val === 'number') values.push(val);
    }
  }
  return computeBuiltinAggregate(config.fn, values);
}

function computeGroups(
  data: Record<string, unknown>[],
  groupBy: GroupByConfig,
): GroupRow[] {
  const groupMap = new Map<unknown, Record<string, unknown>[]>();
  for (const row of data) {
    const key = row[groupBy.field];
    let group = groupMap.get(key);
    if (!group) {
      group = [];
      groupMap.set(key, group);
    }
    group.push(row);
  }

  const groups: GroupRow[] = [];
  for (const [groupKey, rows] of groupMap) {
    const aggregates: Record<string, AggregateResult> = {};
    for (const [name, config] of Object.entries(groupBy.aggregates)) {
      const value = computeAggregateForRows(rows, config);
      aggregates[name] = {
        value,
        formatted: formatAggregateValue(value, config.format),
      };
    }
    groups.push({ groupKey, rows, aggregates });
  }
  return groups;
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
    groupBy: groupByConfig,
    expandable = false,
    childQuery,
    editable = false,
    autoSave = false,
    onRowUpdate,
    onBatchSave,
    columnManagement: columnManagementConfig,
    export: exportConfig,
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
  const childCacheRef = useRef<Map<string, unknown[]>>(new Map());
  const [childLoadingRows, setChildLoadingRows] = useState<Set<string>>(
    new Set(),
  );
  const [childErrors, setChildErrors] = useState<Map<string, Error>>(
    new Map(),
  );
  const childAbortRef = useRef<Map<string, AbortController>>(new Map());
  const bifrostConfig = useContext(BifrostContext);
  const defaultColumnFields = useMemo(() => columns.map((c) => c.field), [columns]);
  const defaultColumnWidths = useMemo(() => {
    const widths: Record<string, number> = {};
    for (const col of columns) {
      widths[col.field] = col.width ?? DEFAULT_COLUMN_WIDTH;
    }
    return widths;
  }, [columns]);

  const [visibleColumns, setVisibleColumns] = useState<string[]>(() =>
    columns.map((c) => c.field),
  );
  const [columnOrder, setColumnOrder] = useState<string[]>(() =>
    columns.map((c) => c.field),
  );
  const [columnWidths, setColumnWidths] = useState<Record<string, number>>(
    () => ({ ...defaultColumnWidths }),
  );
  const [pinnedColumns, setPinnedColumns] = useState<Record<string, PinPosition>>(
    () => ({}),
  );
  const initialColumnPresets = useMemo(() => {
    if (!localStorageConfig?.key) return [];
    return readColumnPresetsFromLocalStorage(localStorageConfig.key);
    // Only read localStorage on mount
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  const [columnPresets, setColumnPresets] = useState<ColumnPreset[]>(initialColumnPresets);

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
    const rows = dataWithComputed as Record<string, unknown>[];
    const result: Record<string, unknown> = {};
    for (const [key, config] of Object.entries(aggregateConfigs)) {
      result[key] = computeAggregateForRows(rows, config);
    }
    return result;
  }, [dataWithComputed, aggregateConfigs]);

  const formattedAggregates = useMemo((): Record<string, AggregateResult> => {
    if (!aggregateConfigs) return {};
    const result: Record<string, AggregateResult> = {};
    for (const [key, config] of Object.entries(aggregateConfigs)) {
      const value = computedAggregates[key];
      result[key] = {
        value,
        formatted: formatAggregateValue(value, config.format),
      };
    }
    return result;
  }, [computedAggregates, aggregateConfigs]);

  const groups = useMemo((): GroupRow[] => {
    if (!groupByConfig) return [];
    return computeGroups(
      dataWithComputed as Record<string, unknown>[],
      groupByConfig,
    );
  }, [dataWithComputed, groupByConfig]);

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

  const fetchChildData = useCallback(
    (rowId: string, parentRow: Record<string, unknown>) => {
      if (!childQuery || !bifrostConfig) return;
      if (childCacheRef.current.has(rowId)) return;
      if (childLoadingRows.has(rowId)) return;

      const existing = childAbortRef.current.get(rowId);
      if (existing) existing.abort();
      const controller = new AbortController();
      childAbortRef.current.set(rowId, controller);

      setChildLoadingRows((prev) => {
        const next = new Set(prev);
        next.add(rowId);
        return next;
      });
      setChildErrors((prev) => {
        if (!prev.has(rowId)) return prev;
        const next = new Map(prev);
        next.delete(rowId);
        return next;
      });

      const parentKeyField = childQuery.parentKeyField ?? rowKey;
      const childFilterField = childQuery.childFilterField ?? parentKeyField;
      const parentValue = parentRow[parentKeyField];

      const query = buildGraphqlQuery(childQuery.query, {
        fields: childQuery.fields,
        filter: { [childFilterField]: { _eq: parentValue as string | number } },
      });

      executeGraphQL<Record<string, unknown[]>>(
        bifrostConfig.endpoint,
        bifrostConfig.headers ?? {},
        query,
        undefined,
        controller.signal,
      )
        .then((data) => {
          const childData = data[childQuery.query] ?? [];
          childCacheRef.current.set(rowId, childData);
          setChildLoadingRows((prev) => {
            const next = new Set(prev);
            next.delete(rowId);
            return next;
          });
          childAbortRef.current.delete(rowId);
        })
        .catch((err: unknown) => {
          if (err instanceof Error && err.name === 'AbortError') return;
          childAbortRef.current.delete(rowId);
          setChildLoadingRows((prev) => {
            const next = new Set(prev);
            next.delete(rowId);
            return next;
          });
          setChildErrors((prev) => {
            const next = new Map(prev);
            next.set(
              rowId,
              err instanceof Error ? err : new Error(String(err)),
            );
            return next;
          });
        });
    },
    [childQuery, bifrostConfig, childLoadingRows, rowKey],
  );

  const getChildData = useCallback(
    (rowId: string): ChildRowData => ({
      data: childCacheRef.current.get(rowId) as unknown[] | null ?? null,
      loading: childLoadingRows.has(rowId),
      error: childErrors.get(rowId) ?? null,
    }),
    [childLoadingRows, childErrors],
  );

  const isChildLoading = useCallback(
    (rowId: string): boolean => childLoadingRows.has(rowId),
    [childLoadingRows],
  );

  const childErrorFn = useCallback(
    (rowId: string): Error | null => childErrors.get(rowId) ?? null,
    [childErrors],
  );

  const clearChildCache = useCallback(
    (rowId?: string) => {
      if (rowId) {
        childCacheRef.current.delete(rowId);
        const controller = childAbortRef.current.get(rowId);
        if (controller) {
          controller.abort();
          childAbortRef.current.delete(rowId);
        }
      } else {
        childCacheRef.current.clear();
        for (const controller of childAbortRef.current.values()) {
          controller.abort();
        }
        childAbortRef.current.clear();
      }
      setChildLoadingRows(new Set());
      setChildErrors(new Map());
    },
    [],
  );

  // Abort in-flight child requests on unmount
  useEffect(() => {
    return () => {
      for (const controller of childAbortRef.current.values()) {
        controller.abort();
      }
    };
  }, []);

  const toggleColumn = useCallback((field: string) => {
    setVisibleColumns((prev) =>
      prev.includes(field) ? prev.filter((f) => f !== field) : [...prev, field],
    );
  }, []);

  const showColumn = useCallback((field: string) => {
    setVisibleColumns((prev) =>
      prev.includes(field) ? prev : [...prev, field],
    );
  }, []);

  const hideColumn = useCallback((field: string) => {
    setVisibleColumns((prev) => prev.filter((f) => f !== field));
  }, []);

  const showAllColumns = useCallback(() => {
    setVisibleColumns([...defaultColumnFields]);
  }, [defaultColumnFields]);

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

  const resizeColumn = useCallback((field: string, width: number) => {
    const clamped = Math.max(30, width);
    setColumnWidths((prev) => ({ ...prev, [field]: clamped }));
  }, []);

  const autoFitColumn = useCallback(
    (field: string) => {
      const col = columns.find((c) => c.field === field);
      if (!col) return;
      const rows = dataWithComputed as Record<string, unknown>[];
      const width = estimateColumnWidth(field, col.header, rows);
      setColumnWidths((prev) => ({ ...prev, [field]: width }));
    },
    [columns, dataWithComputed],
  );

  const autoFitAllColumns = useCallback(() => {
    const rows = dataWithComputed as Record<string, unknown>[];
    const widths: Record<string, number> = {};
    for (const col of columns) {
      widths[col.field] = estimateColumnWidth(col.field, col.header, rows);
    }
    setColumnWidths(widths);
  }, [columns, dataWithComputed]);

  const pinColumn = useCallback((field: string, position: PinPosition) => {
    setPinnedColumns((prev) => {
      if (position === null) {
        const next = { ...prev };
        delete next[field];
        return next;
      }
      return { ...prev, [field]: position };
    });
  }, []);

  const unpinColumn = useCallback((field: string) => {
    setPinnedColumns((prev) => {
      if (!(field in prev)) return prev;
      const next = { ...prev };
      delete next[field];
      return next;
    });
  }, []);

  const saveColumnPreset = useCallback(
    (name: string) => {
      setColumnPresets((prev) => {
        const preset: ColumnPreset = {
          name,
          visibleColumns: [...visibleColumns],
          columnOrder: [...columnOrder],
          columnWidths: { ...columnWidths },
          pinnedColumns: { ...pinnedColumns },
        };
        const existing = prev.findIndex((p) => p.name === name);
        const updated =
          existing >= 0
            ? prev.map((p, i) => (i === existing ? preset : p))
            : [...prev, preset];
        if (localStorageConfig?.key) {
          writeColumnPresetsToLocalStorage(localStorageConfig.key, updated);
        }
        return updated;
      });
    },
    [visibleColumns, columnOrder, columnWidths, pinnedColumns, localStorageConfig?.key],
  );

  const loadColumnPreset = useCallback(
    (name: string) => {
      const preset = columnPresets.find((p) => p.name === name);
      if (!preset) return;
      setVisibleColumns([...preset.visibleColumns]);
      setColumnOrder([...preset.columnOrder]);
      setColumnWidths({ ...preset.columnWidths });
      setPinnedColumns({ ...preset.pinnedColumns });
    },
    [columnPresets],
  );

  const deleteColumnPreset = useCallback(
    (name: string) => {
      setColumnPresets((prev) => {
        const updated = prev.filter((p) => p.name !== name);
        if (localStorageConfig?.key) {
          writeColumnPresetsToLocalStorage(localStorageConfig.key, updated);
        }
        return updated;
      });
    },
    [localStorageConfig?.key],
  );

  const resetColumns = useCallback(() => {
    setVisibleColumns([...defaultColumnFields]);
    setColumnOrder([...defaultColumnFields]);
    setColumnWidths({ ...defaultColumnWidths });
    setPinnedColumns({});
  }, [defaultColumnFields, defaultColumnWidths]);

  const getExportFields = useCallback((): { fields: string[]; headers: string[] } => {
    const orderedVisible = columnOrder.filter((f) => visibleColumns.includes(f));
    const headers = orderedVisible.map((f) => {
      const col = columns.find((c) => c.field === f);
      return col?.header ?? f;
    });
    return { fields: orderedVisible, headers };
  }, [columnOrder, visibleColumns, columns]);

  const getExportRows = useCallback(
    (allPages?: boolean): Record<string, unknown>[] => {
      if (allPages) {
        return dataWithComputed as Record<string, unknown>[];
      }
      return dataWithComputed as Record<string, unknown>[];
    },
    [dataWithComputed],
  );

  const exportCsv = useCallback(
    (allPages?: boolean) => {
      const { fields: exportFields, headers } = getExportFields();
      const rows = getExportRows(allPages);
      const csv = rowsToCsv(rows, exportFields, headers, exportConfig?.formatters);
      const filename = `${exportConfig?.filename ?? table}-export.csv`;
      triggerDownload(csv, filename, 'text/csv;charset=utf-8;');
    },
    [getExportFields, getExportRows, exportConfig, table],
  );

  const exportExcel = useCallback(
    (allPages?: boolean) => {
      const { fields: exportFields, headers } = getExportFields();
      const rows = getExportRows(allPages);
      const tsv = rowsToTsv(rows, exportFields, headers, exportConfig?.formatters);
      const filename = `${exportConfig?.filename ?? table}-export.xls`;
      triggerDownload(tsv, filename, 'application/vnd.ms-excel');
    },
    [getExportFields, getExportRows, exportConfig, table],
  );

  const exportJson = useCallback(
    (allPages?: boolean) => {
      const { fields: exportFields } = getExportFields();
      const rows = getExportRows(allPages);
      const json = rowsToJson(rows, exportFields, exportConfig?.formatters);
      const filename = `${exportConfig?.filename ?? table}-export.json`;
      triggerDownload(json, filename, 'application/json');
    },
    [getExportFields, getExportRows, exportConfig, table],
  );

  const copyToClipboard = useCallback(
    async (allPages?: boolean): Promise<void> => {
      if (typeof navigator === 'undefined' || !navigator.clipboard) return;
      const { fields: exportFields, headers } = getExportFields();
      const rows = getExportRows(allPages);
      const tsv = rowsToTsv(rows, exportFields, headers, exportConfig?.formatters);
      await navigator.clipboard.writeText(tsv);
    },
    [getExportFields, getExportRows, exportConfig],
  );

  const downloadFile = useCallback(
    (format: ExportFormat, allPages?: boolean) => {
      switch (format) {
        case 'csv':
          exportCsv(allPages);
          break;
        case 'excel':
          exportExcel(allPages);
          break;
        case 'json':
          exportJson(allPages);
          break;
      }
    },
    [exportCsv, exportExcel, exportJson],
  );

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
    formattedAggregates,
    groups,
    expansion: {
      expandedRows,
      toggleExpand,
      expandAll,
      collapseAll,
      getChildData,
      fetchChildData,
      isChildLoading,
      childError: childErrorFn,
      clearChildCache,
    },
    columnManagement: {
      visibleColumns,
      toggleColumn,
      showColumn,
      hideColumn,
      showAllColumns,
      columnOrder,
      reorderColumn,
      columnWidths,
      resizeColumn,
      autoFitColumn,
      autoFitAllColumns,
      pinnedColumns,
      pinColumn,
      unpinColumn,
      presets: columnPresets,
      savePreset: saveColumnPreset,
      loadPreset: loadColumnPreset,
      deletePreset: deleteColumnPreset,
      resetColumns,
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
    export: {
      exportCsv,
      exportExcel,
      exportJson,
      copyToClipboard,
      downloadFile,
    },
    loading: queryResult.isLoading,
    error: queryResult.error,
    refetch: queryResult.refetch,
  };
}
