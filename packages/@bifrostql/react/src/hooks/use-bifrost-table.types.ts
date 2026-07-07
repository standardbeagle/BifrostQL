import type { UseBifrostOptions } from './use-bifrost';
import type { SortOption, TableFilter, CompoundFilter } from '../types';

/** Built-in aggregate functions for column summaries. */
export type AggregateFn = 'sum' | 'avg' | 'min' | 'max' | 'count';

/** Display format for aggregate values. */
export type AggregateFormat = 'currency' | 'percentage' | 'number';

/** Sort direction: ascending or descending. */
export type SortDirection = 'asc' | 'desc';

/**
 * Custom comparator function for client-side sorting.
 * Return a negative number if `a` should come before `b`, positive if after, zero if equal.
 */
export type CustomSortFn = (
  a: unknown,
  b: unknown,
  direction: SortDirection,
) => number;

/** Configuration for a column aggregate (footer summary). */
export interface AggregateConfig {
  /** The field to aggregate (defaults to the column's own field). */
  field?: string;
  /** Built-in function name or a custom reducer. */
  fn: AggregateFn | ((values: unknown[]) => unknown);
  /** Display format for the computed value. */
  format?: AggregateFormat | ((value: unknown) => string);
}

/** Configuration for grouping rows by a field. */
export interface GroupByConfig {
  /** The field to group by. */
  field: string;
  /** Aggregate configs to compute per group. */
  aggregates: Record<string, AggregateConfig>;
}

/** A computed aggregate value with its formatted display string. */
export interface AggregateResult {
  /** The raw aggregate value. */
  value: unknown;
  /** The formatted string, or `null` if no formatter was configured. */
  formatted: string | null;
}

/** A group of rows sharing the same group key, with computed aggregates. */
export interface GroupRow {
  /** The value of the grouped field. */
  groupKey: unknown;
  /** All rows in this group. */
  rows: Record<string, unknown>[];
  /** Computed aggregates for this group. */
  aggregates: Record<string, AggregateResult>;
}

/** The type of inline cell editor to render. */
export type EditorType =
  | 'text'
  | 'number'
  | 'select'
  | 'date'
  | 'checkbox'
  | 'textarea';

/**
 * Validation function for inline cell editing.
 * Return `null` if valid, or a string error message if invalid.
 */
export type CellValidator = (
  value: unknown,
  row: Record<string, unknown>,
) => string | null | Promise<string | null>;

/** Configuration for a single table column. */
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

/** Props passed to a custom cell editor component. */
export interface CellEditorProps {
  /** Current cell value. */
  value: unknown;
  /** Update the cell value (does not commit). */
  onChange: (value: unknown) => void;
  /** Commit the current value and close the editor. */
  onCommit: () => void;
  /** Discard changes and close the editor. */
  onCancel: () => void;
  /** The column configuration for this cell. */
  column: ColumnConfig;
  /** The full row data. */
  row: Record<string, unknown>;
}

/** Configuration for persisting table state to `localStorage`. */
export interface LocalStorageConfig {
  /** Storage key prefix. */
  key: string;
  /** Whether to persist filter state. Defaults to `false`. */
  persistFilters?: boolean;
}

/** A named filter preset that can be saved and loaded. */
export interface FilterPreset {
  /** Display name for the preset. */
  name: string;
  /** Simple field filters. */
  filters: TableFilter;
  /** Optional compound filter (AND/OR logic). */
  compoundFilter?: CompoundFilter;
}

/** Sorting state and actions returned by `useBifrostTable`. */
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

/** Filter state and actions returned by `useBifrostTable`. */
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

/** Pagination state and actions returned by `useBifrostTable`. */
export interface PaginationState {
  page: number;
  pageSize: number;
  setPage: (page: number) => void;
  setPageSize: (size: number) => void;
  nextPage: () => void;
  previousPage: () => void;
}

/** Row selection state and actions returned by `useBifrostTable`. */
export interface SelectionState<T = Record<string, unknown>> {
  selectedRows: T[];
  toggleRow: (row: T) => void;
  selectAll: (rows: T[]) => void;
  clearSelection: () => void;
}

/** Configuration for fetching child/detail rows when a parent row is expanded. */
export interface ChildQueryConfig {
  query: string;
  parentKeyField?: string;
  childFilterField?: string;
  fields?: string[];
}

/** State of fetched child data for an expanded row. */
export interface ChildRowData {
  data: unknown[] | null;
  loading: boolean;
  error: Error | null;
}

/** Row expansion state and actions returned by `useBifrostTable`. */
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

/** Position for pinning a column: `'left'`, `'right'`, or `null` (unpinned). */
export type PinPosition = 'left' | 'right' | null;

/** A saved column layout preset. */
export interface ColumnPreset {
  name: string;
  visibleColumns: string[];
  columnOrder: string[];
  columnWidths: Record<string, number>;
  pinnedColumns: Record<string, PinPosition>;
}

/** Feature flags for column management capabilities. */
export interface ColumnManagementConfig {
  resizable?: boolean;
  reorderable?: boolean;
  hideable?: boolean;
  freezable?: boolean;
}

/** Supported export file formats. */
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

/**
 * Export actions derived from the visible, ordered columns and the currently
 * loaded rows.
 *
 * NOTE on `allPages`: the table fetches one page at a time via server-side
 * pagination, so this hook only ever holds the current page's rows. It cannot
 * fetch other pages, so `allPages` cannot be honored here — every export
 * operates on the loaded rows regardless of the flag. Requesting `allPages`
 * emits a one-time dev warning. To export the full result set, either raise the
 * page size so all rows are loaded, or fetch the complete set separately (e.g.
 * `useBifrostInfinite` / a dedicated query) and export that. The flag is
 * retained for forward compatibility with a future fetch-capable export path.
 */
export interface ExportState {
  exportCsv: (allPages?: boolean) => void;
  exportExcel: (allPages?: boolean) => void;
  exportJson: (allPages?: boolean) => void;
  copyToClipboard: (allPages?: boolean) => Promise<void>;
  downloadFile: (format: ExportFormat, allPages?: boolean) => void;
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

export interface VirtualScrollConfig {
  enabled?: boolean;
  rowHeight: number;
  containerHeight: number;
  overscan?: number;
}

export interface VisibleRange {
  startIndex: number;
  endIndex: number;
  overscanStartIndex: number;
  overscanEndIndex: number;
}

export interface VirtualScrollState {
  enabled: boolean;
  visibleRange: VisibleRange;
  totalHeight: number;
  offsetTop: number;
  visibleRows: Record<string, unknown>[];
  scrollToRow: (index: number) => void;
  scrollToTop: () => void;
  scrollToBottom: () => void;
  onScroll: (scrollTop: number) => void;
  scrollTop: number;
  containerHeight: number;
  rowHeight: number;
  isVirtualized: boolean;
}

export interface PerformanceState {
  debouncedSearch: string;
  setSearch: (value: string) => void;
  isSearchPending: boolean;
  requestCount: number;
  lastRequestTime: number | null;
  isStale: boolean;
}

export type Breakpoint = 'xs' | 'sm' | 'md' | 'lg' | 'xl';

export type ColumnPriority = 1 | 2 | 3 | 4 | 5;

export interface ResponsiveColumnConfig {
  field: string;
  priority: ColumnPriority;
  minBreakpoint?: Breakpoint;
}

export interface BreakpointConfig {
  xs: number;
  sm: number;
  md: number;
  lg: number;
  xl: number;
}

export interface CardViewRow<T = Record<string, unknown>> {
  key: string;
  data: T;
  fields: Array<{ field: string; header: string; value: unknown }>;
}

export interface AriaTableProps {
  role: 'grid';
  'aria-label': string;
  'aria-rowcount': number;
  'aria-colcount': number;
  'aria-multiselectable'?: boolean;
}

export interface AriaRowProps {
  role: 'row';
  'aria-rowindex': number;
  'aria-selected'?: boolean;
  'aria-expanded'?: boolean;
  tabIndex?: number;
}

export interface AriaCellProps {
  role: 'gridcell';
  'aria-colindex': number;
  'aria-readonly'?: boolean;
  tabIndex?: number;
}

export interface AriaHeaderCellProps {
  role: 'columnheader';
  'aria-colindex': number;
  'aria-sort'?: 'ascending' | 'descending' | 'none';
  tabIndex?: number;
}

export interface AriaLiveRegionProps {
  role: 'status';
  'aria-live': 'polite';
  'aria-atomic': boolean;
}

export interface FocusPosition {
  rowIndex: number;
  colIndex: number;
}

export interface KeyboardNavigationState {
  focusedCell: FocusPosition | null;
  setFocusedCell: (pos: FocusPosition | null) => void;
  handleKeyDown: (e: {
    key: string;
    preventDefault: () => void;
    shiftKey?: boolean;
  }) => void;
}

export interface AccessibilityState {
  getTableProps: (label?: string) => AriaTableProps;
  getRowProps: (rowIndex: number, rowKey?: string) => AriaRowProps;
  getCellProps: (colIndex: number, field?: string) => AriaCellProps;
  getHeaderCellProps: (colIndex: number, field?: string) => AriaHeaderCellProps;
  getLiveRegionProps: () => AriaLiveRegionProps;
  announcement: string;
  keyboard: KeyboardNavigationState;
}

export interface ResponsiveState<T = Record<string, unknown>> {
  currentBreakpoint: Breakpoint;
  isMobile: boolean;
  isTablet: boolean;
  isDesktop: boolean;
  responsiveVisibleColumns: string[];
  cardViewData: CardViewRow<T>[];
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
  tableLabel?: string;
  responsiveColumns?: ResponsiveColumnConfig[];
  breakpoints?: Partial<BreakpointConfig>;
  virtualScroll?: VirtualScrollConfig;
  searchDebounceMs?: number;
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
  a11y: AccessibilityState;
  responsive: ResponsiveState<T>;
  virtualScroll: VirtualScrollState;
  performance: PerformanceState;
  loading: boolean;
  error: Error | null;
  refetch: () => void;
}
