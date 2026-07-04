import { describe, it, expect } from 'vitest';
import {
  clientFilterRows,
  clientSortRows,
  computeAggregateForRows,
  computeBuiltinAggregate,
  computeGroups,
  countActiveFilters,
  defaultCompare,
  estimateColumnWidth,
  formatAggregateValue,
  matchesFieldFilter,
  matchesTableFilter,
  mergeFiltersForQuery,
} from './table-client-ops';
import type { ColumnConfig } from '../hooks/use-bifrost-table.types';

const cols: ColumnConfig[] = [
  { field: 'name', header: 'Name' },
  { field: 'age', header: 'Age' },
];

describe('defaultCompare', () => {
  it('sorts nullish values to the end', () => {
    expect(defaultCompare(null, 1)).toBeGreaterThan(0);
    expect(defaultCompare(1, null)).toBeLessThan(0);
    expect(defaultCompare(null, null)).toBe(0);
  });

  it('compares numbers numerically and strings lexically', () => {
    expect(defaultCompare(2, 10)).toBeLessThan(0);
    expect(defaultCompare('b', 'a')).toBeGreaterThan(0);
  });
});

describe('clientSortRows', () => {
  it('returns rows unchanged when there is no sort', () => {
    const rows = [{ age: 2 }, { age: 1 }];
    expect(clientSortRows(rows, [], cols)).toBe(rows);
  });

  it('sorts ascending and descending', () => {
    const rows = [{ age: 3 }, { age: 1 }, { age: 2 }];
    const asc = clientSortRows(
      rows,
      [{ field: 'age', direction: 'asc' }],
      cols,
    );
    expect(asc.map((r) => r.age)).toEqual([1, 2, 3]);
    const desc = clientSortRows(
      rows,
      [{ field: 'age', direction: 'desc' }],
      cols,
    );
    expect(desc.map((r) => r.age)).toEqual([3, 2, 1]);
  });

  it('honors a column custom comparator', () => {
    const custom: ColumnConfig[] = [
      {
        field: 'age',
        header: 'Age',
        customSort: (a, b) => (a as number) - (b as number),
      },
    ];
    const rows = [{ age: 3 }, { age: 1 }];
    const sorted = clientSortRows(
      rows,
      [{ field: 'age', direction: 'desc' }],
      custom,
    );
    // custom comparator ignores direction here, so ascending result
    expect(sorted.map((r) => r.age)).toEqual([1, 3]);
  });
});

describe('matchesFieldFilter', () => {
  it('supports comparison, membership, and string operators', () => {
    expect(matchesFieldFilter(5, { _gt: 3 })).toBe(true);
    expect(matchesFieldFilter(5, { _lte: 4 })).toBe(false);
    expect(matchesFieldFilter('a', { _in: ['a', 'b'] })).toBe(true);
    expect(matchesFieldFilter('Hello', { _contains: 'ell' })).toBe(true);
    expect(matchesFieldFilter('Hello', { _starts_with: 'he' })).toBe(true);
    expect(matchesFieldFilter(null, { _null: true })).toBe(true);
    expect(matchesFieldFilter(5, { _between: [1, 10] })).toBe(true);
    expect(matchesFieldFilter(50, { _between: [1, 10] })).toBe(false);
  });
});

describe('matchesTableFilter', () => {
  it('matches equality, null, and nested field filters', () => {
    expect(matchesTableFilter({ a: 1 }, { a: 1 })).toBe(true);
    expect(matchesTableFilter({ a: null }, { a: null })).toBe(true);
    expect(matchesTableFilter({ a: 5 }, { a: { _gt: 3 } })).toBe(true);
    expect(matchesTableFilter({ a: 2 }, { a: { _gt: 3 } })).toBe(false);
  });
});

describe('clientFilterRows', () => {
  it('applies simple and compound filters', () => {
    const rows = [{ age: 1 }, { age: 5 }, { age: 9 }];
    expect(clientFilterRows(rows, { age: { _gte: 5 } }, null)).toEqual([
      { age: 5 },
      { age: 9 },
    ]);
    expect(
      clientFilterRows(rows, {}, { _or: [{ age: 1 }, { age: 9 }] }),
    ).toEqual([{ age: 1 }, { age: 9 }]);
  });
});

describe('aggregates', () => {
  it('computes builtin aggregates', () => {
    expect(computeBuiltinAggregate('sum', [1, 2, 3])).toBe(6);
    expect(computeBuiltinAggregate('avg', [2, 4])).toBe(3);
    expect(computeBuiltinAggregate('min', [3, 1, 2])).toBe(1);
    expect(computeBuiltinAggregate('max', [3, 1, 2])).toBe(3);
    expect(computeBuiltinAggregate('count', [3, 1])).toBe(2);
    expect(computeBuiltinAggregate('sum', [])).toBe(0);
  });

  it('computeAggregateForRows counts rows and reduces numeric fields', () => {
    const rows = [{ n: 1 }, { n: 3 }, { n: 'x' }];
    expect(computeAggregateForRows(rows, { field: 'n', fn: 'count' })).toBe(3);
    expect(computeAggregateForRows(rows, { field: 'n', fn: 'sum' })).toBe(4);
    expect(
      computeAggregateForRows(rows, {
        field: 'n',
        fn: (vals) => (vals as unknown[]).length,
      }),
    ).toBe(3);
  });

  it('formatAggregateValue handles formats and custom functions', () => {
    expect(formatAggregateValue(5, undefined)).toBeNull();
    expect(formatAggregateValue(1000, 'number')).toBe((1000).toLocaleString());
    expect(formatAggregateValue(5, (v) => `#${v}`)).toBe('#5');
  });
});

describe('computeGroups', () => {
  it('groups rows by field and computes aggregates', () => {
    const data = [
      { cat: 'a', n: 1 },
      { cat: 'a', n: 2 },
      { cat: 'b', n: 5 },
    ];
    const groups = computeGroups(data, {
      field: 'cat',
      aggregates: { total: { field: 'n', fn: 'sum' } },
    });
    expect(groups).toHaveLength(2);
    const a = groups.find((g) => g.groupKey === 'a')!;
    expect(a.rows).toHaveLength(2);
    expect(a.aggregates.total.value).toBe(3);
  });
});

describe('countActiveFilters', () => {
  it('counts simple filter keys plus compound clauses', () => {
    expect(countActiveFilters({ a: 1, b: 2 }, null)).toBe(2);
    expect(
      countActiveFilters({ a: 1 }, { _and: [{ b: 1 }], _or: [{ c: 1 }] }),
    ).toBe(3);
  });
});

describe('mergeFiltersForQuery', () => {
  it('returns undefined when nothing is set', () => {
    expect(mergeFiltersForQuery({}, null)).toBeUndefined();
  });

  it('returns filters or compound alone, or merges both under _and', () => {
    expect(mergeFiltersForQuery({ a: 1 }, null)).toEqual({ a: 1 });
    const compound = { _or: [{ b: 1 }] };
    expect(mergeFiltersForQuery({}, compound)).toBe(compound);
    expect(mergeFiltersForQuery({ a: 1 }, compound)).toEqual({
      _and: [{ a: 1 }, compound],
    });
  });
});

describe('estimateColumnWidth', () => {
  it('clamps to the min/max bounds based on content length', () => {
    expect(estimateColumnWidth('x', 'H', [])).toBe(50);
    const wide = estimateColumnWidth('x', 'H', [{ x: 'a'.repeat(200) }]);
    expect(wide).toBe(500);
  });
});
