import { useMemo } from 'react';
import { useBifrostQuery } from './use-bifrost-query';
import {
  resolveClientSideFilterConfig,
  resolveClientSideSortConfig,
  resolveUrlSyncConfig,
} from '../utils/table-breakpoints';
import { mergeFiltersForQuery } from '../utils/table-client-ops';
import { useTableA11y } from './internal/use-table-a11y';
import { useTableColumnManagement } from './internal/use-table-column-management';
import { useTableData } from './internal/use-table-data';
import { useTableEditing } from './internal/use-table-editing';
import { useTableExpansion } from './internal/use-table-expansion';
import { useTableExport } from './internal/use-table-export';
import { useTablePerformance } from './internal/use-table-performance';
import { useTableQueryState } from './internal/use-table-query-state';
import { useTableResponsive } from './internal/use-table-responsive';
import { useTableSelection } from './internal/use-table-selection';
import { useVirtualScroll } from './internal/use-virtual-scroll';
import type {
  UseBifrostTableOptions,
  UseBifrostTableResult,
} from './use-bifrost-table.types';

export type * from './use-bifrost-table.types';

/**
 * All-in-one headless table state management hook.
 *
 * Provides sorting, filtering, pagination, row selection, column management,
 * URL synchronization, computed columns, aggregates, inline editing, export,
 * accessibility (ARIA), responsive breakpoints, virtual scrolling, and
 * performance optimizations.
 *
 * Internally uses {@link useBifrostQuery} for data fetching and composes a set
 * of focused feature hooks (see `hooks/internal/`); this function is a thin
 * orchestrator that wires their shared state together.
 *
 * Must be used within a {@link BifrostProvider}.
 *
 * @typeParam T - The row data type.
 * @param options - Table configuration including query, columns, and feature flags.
 * @returns A comprehensive state object with all table features.
 *
 * @example
 * ```tsx
 * const table = useBifrostTable<User>({
 *   query: 'users',
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
    defaultFilters,
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

  const queryResult = useBifrostQuery<T[]>(table, {
    fields,
    filter: serverFilter,
    sort: serverSort,
    pagination: { limit: pageSize, offset: page * pageSize },
    ...bifrostOptions,
  });

  const { dataWithComputed, computedAggregates, formattedAggregates, groups } =
    useTableData<T>({
      rawData: queryResult.data,
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

  const performance = useTablePerformance({
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
    performance,
    loading: queryResult.isLoading,
    error: queryResult.error,
    refetch: queryResult.refetch,
  };
}
