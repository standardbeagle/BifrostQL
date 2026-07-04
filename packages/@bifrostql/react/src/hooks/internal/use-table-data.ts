import { useMemo } from 'react';
import {
  clientFilterRows,
  clientSortRows,
  computeAggregateForRows,
  computeGroups,
  formatAggregateValue,
} from '../../utils/table-client-ops';
import type { SortOption, TableFilter, CompoundFilter } from '../../types';
import type {
  AggregateConfig,
  AggregateResult,
  ColumnConfig,
  GroupByConfig,
  GroupRow,
} from '../use-bifrost-table.types';

interface ClientOpConfig {
  enabled: boolean;
  threshold: number;
}

export interface UseTableDataOptions<T> {
  rawData: T[] | undefined;
  columns: ColumnConfig[];
  sort: SortOption[];
  debouncedFilters: TableFilter;
  compoundFilter: CompoundFilter | null;
  clientSortConfig: ClientOpConfig;
  clientFilterConfig: ClientOpConfig;
  aggregateConfigs: Record<string, AggregateConfig> | undefined;
  groupByConfig: GroupByConfig | undefined;
}

export interface UseTableDataResult<T> {
  dataWithComputed: T[];
  computedAggregates: Record<string, unknown>;
  formattedAggregates: Record<string, AggregateResult>;
  groups: GroupRow[];
}

/**
 * Derives the presented row set from the raw query data by applying computed
 * columns, then client-side filtering and sorting (when enabled and under the
 * configured threshold), and computes aggregates and group summaries.
 */
export function useTableData<T = Record<string, unknown>>({
  rawData,
  columns,
  sort,
  debouncedFilters,
  compoundFilter,
  clientSortConfig,
  clientFilterConfig,
  aggregateConfigs,
  groupByConfig,
}: UseTableDataOptions<T>): UseTableDataResult<T> {
  const computedColumns = useMemo(
    () => columns.filter((c) => c.computed),
    [columns],
  );

  const dataLength = (rawData ?? []).length;

  const shouldClientSort =
    clientSortConfig.enabled && dataLength <= clientSortConfig.threshold;

  const shouldClientFilter =
    clientFilterConfig.enabled && dataLength <= clientFilterConfig.threshold;

  const dataWithComputed = useMemo(() => {
    const rawRows = rawData ?? [];
    let processed = rawRows;
    if (computedColumns.length > 0) {
      processed = rawRows.map((row) => {
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
    rawData,
    computedColumns,
    shouldClientFilter,
    debouncedFilters,
    compoundFilter,
    shouldClientSort,
    sort,
    columns,
  ]);

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

  return { dataWithComputed, computedAggregates, formattedAggregates, groups };
}
