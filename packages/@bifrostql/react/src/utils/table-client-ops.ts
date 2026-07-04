import type {
  AdvancedFilter,
  CompoundFilter,
  FieldFilter,
  SortOption,
  TableFilter,
} from '../types';
import type {
  AggregateConfig,
  AggregateFn,
  AggregateFormat,
  AggregateResult,
  ColumnConfig,
  GroupByConfig,
  GroupRow,
} from '../hooks/use-bifrost-table.types';

export const DEFAULT_COLUMN_WIDTH = 150;

export function estimateColumnWidth(
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

export function countActiveFilters(
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

export function defaultCompare(a: unknown, b: unknown): number {
  if (a === b) return 0;
  if (a === null || a === undefined) return 1;
  if (b === null || b === undefined) return -1;
  if (typeof a === 'number' && typeof b === 'number') return a - b;
  return String(a).localeCompare(String(b));
}

export function clientSortRows<T>(
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

export function matchesFieldFilter(
  value: unknown,
  filter: FieldFilter,
): boolean {
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

export function matchesTableFilter(
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

export function matchesAdvancedFilter(
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

export function clientFilterRows<T>(
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

export function computeBuiltinAggregate(
  fn: AggregateFn,
  values: number[],
): number {
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

export function formatAggregateValue(
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

export function computeAggregateForRows(
  rows: Record<string, unknown>[],
  config: AggregateConfig,
): unknown {
  if (typeof config.fn === 'function') {
    const values = rows.map((row) => (config.field ? row[config.field] : row));
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

export function computeGroups(
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

export function mergeFiltersForQuery(
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
