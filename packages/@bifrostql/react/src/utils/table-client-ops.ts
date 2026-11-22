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

// The server writes the BigInt and Decimal scalars as canonical decimal
// strings ("-12", "9007199254740993", "19.990"): no leading zeros, a fraction
// only when the value has a scale. Only that exact shape is read as a number,
// so text such as "007" or "1e3" stays text.
const CANONICAL_DECIMAL = /^-?(?:0|[1-9]\d*)(?:\.(\d+))?$/;

/** A decimal held exactly: `coefficient / 10^scale`. */
interface ExactDecimal {
  coefficient: bigint;
  scale: number;
}

/**
 * The exact decimal a cell holds, or null: a safe-integer number, or a
 * canonical decimal string (a BigInt or Decimal column).
 */
function exactDecimal(value: unknown): ExactDecimal | null {
  if (typeof value === 'number') {
    return Number.isSafeInteger(value)
      ? { coefficient: BigInt(value), scale: 0 }
      : null;
  }
  if (typeof value !== 'string') return null;
  const match = CANONICAL_DECIMAL.exec(value);
  if (!match) return null;
  return {
    coefficient: BigInt(value.replace('.', '')),
    scale: match[1]?.length ?? 0,
  };
}

function rescale(value: ExactDecimal, scale: number): bigint {
  return value.coefficient * 10n ** BigInt(scale - value.scale);
}

function compareExact(a: ExactDecimal, b: ExactDecimal): number {
  const scale = Math.max(a.scale, b.scale);
  const left = rescale(a, scale);
  const right = rescale(b, scale);
  return left < right ? -1 : left > right ? 1 : 0;
}

/** Canonical decimal text for an exact value, the shape the server writes. */
function exactDecimalText(value: ExactDecimal): string {
  if (value.scale === 0) return value.coefficient.toString();
  const negative = value.coefficient < 0n;
  const digits = (negative ? -value.coefficient : value.coefficient)
    .toString()
    .padStart(value.scale + 1, '0');
  const point = digits.length - value.scale;
  return `${negative ? '-' : ''}${digits.slice(0, point)}.${digits.slice(point)}`;
}

/** Numeric value for client aggregation: numbers, and BigInt/Decimal columns' strings. */
function numericValue(value: unknown): number | ExactDecimal | null {
  if (typeof value === 'number') return value;
  return typeof value === 'string' ? exactDecimal(value) : null;
}

export function defaultCompare(a: unknown, b: unknown): number {
  if (a === b) return 0;
  if (a === null || a === undefined) return 1;
  if (b === null || b === undefined) return -1;
  if (typeof a === 'number' && typeof b === 'number') return a - b;
  // BigInt and Decimal columns arrive as decimal strings; text order would put
  // "10" before "9", and Number() rounds past 15-17 significant digits.
  const ad = exactDecimal(a);
  const bd = exactDecimal(b);
  if (ad !== null && bd !== null) return compareExact(ad, bd);
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
 * Reduces numeric cells. Numbers reduce as numbers. When any cell is a BigInt or
 * Decimal column's string, sum/min/max are computed exactly: an integer result
 * is returned as a number while that is exact, anything else as the decimal
 * string the server would send. avg is a number either way.
 */
export function computeBuiltinAggregate(
  fn: AggregateFn,
  values: Array<number | ExactDecimal>,
): number | string {
  if (values.length === 0) return 0;
  if (fn === 'count') return values.length;
  if (values.some((v) => typeof v !== 'number')) {
    const exact = values.map((v) =>
      typeof v === 'number' ? exactDecimal(v) : v,
    );
    if (exact.every((v): v is ExactDecimal => v !== null))
      return exactAggregate(fn, exact);
    // A fractional number among the exact values: no exact result exists.
    return computeBuiltinAggregate(
      fn,
      values.map((v) =>
        typeof v === 'number' ? v : Number(exactDecimalText(v)),
      ),
    );
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

function exactAggregate(
  fn: Exclude<AggregateFn, 'count'>,
  values: ExactDecimal[],
): number | string {
  const sum = (): ExactDecimal => {
    const scale = Math.max(...values.map((v) => v.scale));
    return {
      coefficient: values.reduce((a, v) => a + rescale(v, scale), 0n),
      scale,
    };
  };
  let result: ExactDecimal;
  switch (fn) {
    case 'avg':
      return Number(exactDecimalText(sum())) / values.length;
    case 'sum':
      result = sum();
      break;
    case 'min':
      result = values.reduce((a, b) => (compareExact(b, a) < 0 ? b : a));
      break;
    case 'max':
      result = values.reduce((a, b) => (compareExact(b, a) > 0 ? b : a));
      break;
  }
  if (result.scale === 0) {
    const asNumber = Number(result.coefficient);
    if (Number.isSafeInteger(asNumber)) return asNumber;
  }
  return exactDecimalText(result);
}

export function formatAggregateValue(
  value: unknown,
  format: AggregateFormat | ((value: unknown) => string) | undefined,
): string | null {
  if (!format) return null;
  if (typeof format === 'function') return format(value);
  // An exact aggregate (or cell) is canonical decimal text. Intl formats that
  // string itself, so no digit is rounded through a double.
  const formattable: number | Intl.StringNumericLiteral | null =
    typeof value === 'number'
      ? value
      : typeof value === 'string' && CANONICAL_DECIMAL.test(value)
        ? (value as Intl.StringNumericLiteral)
        : null;
  if (formattable === null) return String(value ?? '');
  switch (format) {
    case 'currency':
      return new Intl.NumberFormat(undefined, {
        style: 'currency',
        currency: 'USD',
      }).format(formattable);
    case 'percentage':
      return new Intl.NumberFormat(undefined, {
        style: 'percent',
        minimumFractionDigits: 1,
        maximumFractionDigits: 1,
      }).format(formattable);
    case 'number':
      return new Intl.NumberFormat(undefined).format(formattable);
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
  const values: Array<number | ExactDecimal> = [];
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
