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

// The server writes the BigInt scalar as a canonical decimal string ("-12",
// "9007199254740993"): no sign on zero, no leading zeros. Only that exact shape
// is read as an integer, so text such as "007" or "1e3" stays text.
const CANONICAL_INTEGER = /^(?:0|-?[1-9]\d*)$/;

/**
 * The exact integer a cell holds, or null: a safe-integer number, or a
 * canonical decimal string (a BigInt column).
 */
function exactInteger(value: unknown): bigint | null {
  if (typeof value === 'number') return Number.isSafeInteger(value) ? BigInt(value) : null;
  if (typeof value === 'string' && CANONICAL_INTEGER.test(value)) return BigInt(value);
  return null;
}

/** Numeric value for client aggregation: numbers, and BigInt columns' strings. */
function numericValue(value: unknown): number | bigint | null {
  if (typeof value === 'number') return value;
  return typeof value === 'string' ? exactInteger(value) : null;
}

export function defaultCompare(a: unknown, b: unknown): number {
  if (a === b) return 0;
  if (a === null || a === undefined) return 1;
  if (b === null || b === undefined) return -1;
  if (typeof a === 'number' && typeof b === 'number') return a - b;
  // BigInt columns arrive as decimal strings; text order would put "10" before "9".
  const ai = exactInteger(a);
  const bi = exactInteger(b);
  if (ai !== null && bi !== null) return ai < bi ? -1 : ai > bi ? 1 : 0;
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

/**
 * Reduces numeric cells. Numbers reduce as numbers. When any cell is a BigInt
 * column's integer, sum/min/max are computed exactly and returned as a number
 * while that is exact, else as the decimal string the server would send; avg is
 * a number either way.
 */
export function computeBuiltinAggregate(
  fn: AggregateFn,
  values: Array<number | bigint>,
): number | string {
  if (values.length === 0) return 0;
  if (fn === 'count') return values.length;
  if (values.some((v) => typeof v === 'bigint')) {
    const exact = values.map((v) => (typeof v === 'bigint' ? v : exactInteger(v)));
    if (exact.every((v): v is bigint => v !== null)) return bigintAggregate(fn, exact);
    // A fractional number among the integers: no exact result exists.
    return computeBuiltinAggregate(fn, values.map(Number));
  }
  const nums = values as number[];
  switch (fn) {
    case 'sum':
      return nums.reduce((a, b) => a + b, 0);
    case 'avg':
      return nums.reduce((a, b) => a + b, 0) / nums.length;
    case 'min':
      return Math.min(...nums);
    case 'max':
      return Math.max(...nums);
  }
}

function bigintAggregate(fn: Exclude<AggregateFn, 'count'>, values: bigint[]): number | string {
  const sum = () => values.reduce((a, b) => a + b, 0n);
  let result: bigint;
  switch (fn) {
    case 'avg':
      return Number(sum()) / values.length;
    case 'sum':
      result = sum();
      break;
    case 'min':
      result = values.reduce((a, b) => (b < a ? b : a));
      break;
    case 'max':
      result = values.reduce((a, b) => (b > a ? b : a));
      break;
  }
  const asNumber = Number(result);
  return Number.isSafeInteger(asNumber) ? asNumber : result.toString();
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
  const values: Array<number | bigint> = [];
  if (config.field) {
    for (const row of rows) {
      const val = numericValue(row[config.field]);
      if (val !== null) values.push(val);
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
