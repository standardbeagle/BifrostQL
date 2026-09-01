import { useMemo } from 'react';
import type { TableFilter } from '../types';
import { useBifrostQuery } from './use-bifrost-query';
import {
  resolveClientSideFilterConfig,
  resolveClientSideSortConfig,
  resolveUrlSyncConfig,
} from '../utils/table-option-resolvers';
import { mergeFiltersForQuery } from '../utils/table-client-ops';
import { useTableA11y } from './internal/use-table-a11y';
import { useTableColumnManagement } from './internal/use-table-column-management';
import { useTableData } from './internal/use-table-data';
import { useTableEditing } from './internal/use-table-editing';
import { useTableExpansion } from './internal/use-table-expansion';
import { useTableExport } from './internal/use-table-export';
import { useTableSearch } from './internal/use-table-search';
import { useTableQueryState } from './internal/use-table-query-state';
import { useTableResponsive } from './internal/use-table-responsive';
import { useTableSelection } from './internal/use-table-selection';
import { useVirtualScroll } from './internal/use-table-virtual-scroll';
import type {
  UseBifrostTableOptions,
  UseBifrostTableResult,
} from './use-bifrost-table.types';

export type * from './use-bifrost-table.types';

// Shared frozen defaults. A `= []` / `= {}` default parameter allocates a new
// value on every render, and these feed effect dependency arrays (notably the
// popstate listener in useTableQueryState) — a fresh identity each render tore
// the listener down and re-added it on every single render. The sort default
// is `readonly never[]` so the one shared instance satisfies every
// `SortOptionFor<T>` instantiation without a cast.
const NO_DEFAULT_SORT: readonly never[] = [];
const NO_DEFAULT_FILTER: TableFilter = {};
// Frozen at runtime so an accidental in-place mutation of a shared default
// fails loudly instead of leaking into every table on the page.
Object.freeze(NO_DEFAULT_SORT);
Object.freeze(NO_DEFAULT_FILTER);

/**
 * All-in-one headless table state management hook.
 *
 * Provides sorting, filtering, pagination, row selection, column management,
 * URL synchronization, computed columns, aggregates, inline editing, export,
 * accessibility (ARIA), responsive breakpoints, virtual scrolling, and
 * debounced search.
 *
 * Internally uses {@link useBifrostQuery} for data fetching and composes a set
 * of focused feature hooks (see `hooks/internal/`); this function is a thin
 * orchestrator that wires their shared state together.
 *
 * Must be used within a {@link BifrostProvider}.
 *
 * @typeParam T - The row data type.
 * @param options - Table configuration including table name, columns, and feature flags.
 * @returns A comprehensive state object with all table features.
 *
 * @example
 * ```tsx
 * const table = useBifrostTable<User>({
 *   table: 'users',
 *   columns: [
 *     { field: 'id', header: 'ID', sortable: true },
 *     { field: 'name', header: 'Name', sortable: true, filterable: true },
 *   ],
 *   fields: ['id', 'name', 'email'],
 *   pagination: { pageSize: 25 },
 *   defaultSort: [{ field: 'name', direction: 'asc' }],
 *   urlSync: true,
 * });
 * ```
 */
export function useBifrostTable<T = Record<string, unknown>>(
  // NoInfer: the row type is opt-in via the explicit type argument; without it
  // TS would reverse-infer T from `fields` literals at untyped call sites.
  options: UseBifrostTableOptions<NoInfer<T>>,
): UseBifrostTableResult<T> {
  const {
    table,
    columns,
    fields: fieldsProp,
    pagination: paginationConfig,
    defaultSort = NO_DEFAULT_SORT,
    defaultFilter = NO_DEFAULT_FILTER,
    filter: controlledFilter,
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
    onSaveError,
    columnManagement: columnManagementConfig,
    export: exportConfig,
    tableLabel,
    responsiveColumns,
    breakpoints: breakpointsProp,
    virtualScroll: virtualScrollConfig,
    searchDebounceMs = 300,
    ...bifrostOptions
  } = options;

  const syncConfig = resolveUrlSyncConfig(urlSync);
  const clientSortConfig = resolveClientSideSortConfig(clientSideSortProp);
  const clientFilterConfig =
    resolveClientSideFilterConfig(clientSideFilterProp);
  const initialPageSize = paginationConfig?.pageSize ?? 25;

  const queryState = useTableQueryState({
    columns,
    multiSort,
    defaultSort,
    defaultFilter,
    controlledFilter,
    syncConfig,
    localStorageConfig,
    initialPageSize,
    filterDebounceMs,
  });
  const {
    sort,
    debouncedFilters,
    compoundFilter,
    page,
    pageSize,
    activeFilterCount,
  } = queryState;

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

  // The internal fetch runs in the untyped field-name space: sort/filter state
  // also carries computed-column keys that are not on the row type. The row
  // shape is asserted back to T at the single hand-off below.
  const queryResult = useBifrostQuery<Record<string, unknown>[]>(table, {
    fields,
    filter: serverFilter,
    sort: serverSort,
    pagination: { limit: pageSize, offset: page * pageSize },
    ...bifrostOptions,
  });

  const { dataWithComputed, computedAggregates, formattedAggregates, groups } =
    useTableData<T>({
      rawData: queryResult.data as T[] | undefined,
      columns,
      sort,
      debouncedFilters,
      compoundFilter,
      clientSortConfig,
      clientFilterConfig,
      aggregateConfigs,
      groupByConfig,
    });

  const dataAsRecords = dataWithComputed as Record<string, unknown>[];

  const { selection, toggleRow } = useTableSelection<T>(rowKey);

  const { expansion, expandedRows } = useTableExpansion({
    expandable,
    childQuery,
    rowKey,
  });

  const { columnManagement, visibleColumns, columnOrder } =
    useTableColumnManagement({
      columns,
      data: dataAsRecords,
      localStorageConfig,
      config: columnManagementConfig,
    });

  const exportState = useTableExport({
    columnOrder,
    visibleColumns,
    columns,
    data: dataAsRecords,
    exportConfig,
    table,
  });

  const {
    editing,
    editableColumnSet,
    editingCell,
    startEditing,
    cancelEditing,
  } = useTableEditing<T>({
    columns,
    editable,
    data: dataWithComputed,
    rowKey,
    autoSave,
    onRowUpdate,
    onBatchSave,
    onSaveError,
    refetch: queryResult.refetch,
  });

  const a11y = useTableA11y<T>({
    sort,
    columns,
    activeFilterCount,
    data: dataWithComputed,
    visibleColumns,
    editableColumnSet,
    rowKey,
    selectedRows: selection.selectedRows,
    expandedRows,
    editingCell,
    tableLabel,
    table,
    startEditing,
    cancelEditing,
    toggleRow,
  });

  const responsive = useTableResponsive<T>({
    breakpointsProp,
    responsiveColumns,
    visibleColumns,
    columns,
    rowKey,
    data: dataWithComputed,
  });

  const virtualScroll = useVirtualScroll({
    config: virtualScrollConfig,
    data: dataAsRecords,
  });

  const search = useTableSearch({
    searchDebounceMs,
    isLoading: queryResult.isLoading,
    dataLength: dataWithComputed.length,
  });

  return {
    data: dataWithComputed,
    columns,
    sorting: queryState.sorting,
    filters: queryState.filtersApi,
    pagination: queryState.pagination,
    selection,
    aggregates: computedAggregates,
    formattedAggregates,
    groups,
    expansion,
    columnManagement,
    editing,
    export: exportState,
    a11y,
    responsive,
    virtualScroll,
    search,
    totalRows: queryResult.total,
    loading: queryResult.isLoading,
    error: queryResult.error,
    refetch: queryResult.refetch,
  };
}
