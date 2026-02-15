import { describe, it, expect } from 'vitest';
import { parseTableParams } from './parse-url-params';

describe('parseTableParams', () => {
  it('returns empty options for empty params', () => {
    const result = parseTableParams({});
    expect(result).toEqual({});
  });

  it('parses ascending sort', () => {
    const result = parseTableParams({ sort: 'name' });
    expect(result.sort).toEqual([{ field: 'name', direction: 'asc' }]);
  });

  it('parses descending sort with - prefix', () => {
    const result = parseTableParams({ sort: '-created_at' });
    expect(result.sort).toEqual([{ field: 'created_at', direction: 'desc' }]);
  });

  it('parses multiple sort fields', () => {
    const result = parseTableParams({ sort: '-created_at,name' });
    expect(result.sort).toEqual([
      { field: 'created_at', direction: 'desc' },
      { field: 'name', direction: 'asc' },
    ]);
  });

  it('ignores empty sort entries', () => {
    const result = parseTableParams({ sort: ',,name,,' });
    expect(result.sort).toEqual([{ field: 'name', direction: 'asc' }]);
  });

  it('parses limit', () => {
    const result = parseTableParams({ limit: '25' });
    expect(result.pagination).toEqual({ limit: 25 });
  });

  it('parses offset', () => {
    const result = parseTableParams({ offset: '50' });
    expect(result.pagination).toEqual({ offset: 50 });
  });

  it('parses both limit and offset', () => {
    const result = parseTableParams({ limit: '10', offset: '20' });
    expect(result.pagination).toEqual({ limit: 10, offset: 20 });
  });

  it('ignores non-numeric limit', () => {
    const result = parseTableParams({ limit: 'abc' });
    expect(result.pagination).toBeUndefined();
  });

  it('ignores zero limit', () => {
    const result = parseTableParams({ limit: '0' });
    expect(result.pagination).toBeUndefined();
  });

  it('ignores negative limit', () => {
    const result = parseTableParams({ limit: '-5' });
    expect(result.pagination).toBeUndefined();
  });

  it('parses equality filter shorthand', () => {
    const result = parseTableParams({ 'filter[status]': 'active' });
    expect(result.filter).toEqual({ status: 'active' });
  });

  it('parses operator filter', () => {
    const result = parseTableParams({ 'filter[age][gte]': '18' });
    expect(result.filter).toEqual({ age: { _gte: 18 } });
  });

  it('parses multiple operators on the same field', () => {
    const result = parseTableParams({
      'filter[price][gte]': '10',
      'filter[price][lt]': '100',
    });
    expect(result.filter).toEqual({ price: { _gte: 10, _lt: 100 } });
  });

  it('parses null filter value', () => {
    const result = parseTableParams({ 'filter[deleted_at][null]': 'true' });
    expect(result.filter).toEqual({ deleted_at: { _null: true } });
  });

  it('parses boolean filter value', () => {
    const result = parseTableParams({ 'filter[active][eq]': 'true' });
    expect(result.filter).toEqual({ active: { _eq: true } });
  });

  it('parses comma-separated in filter', () => {
    const result = parseTableParams({ 'filter[status][in]': 'active,pending' });
    expect(result.filter).toEqual({ status: { _in: ['active', 'pending'] } });
  });

  it('parses numeric values in comma-separated list', () => {
    const result = parseTableParams({ 'filter[id][in]': '1,2,3' });
    expect(result.filter).toEqual({ id: { _in: [1, 2, 3] } });
  });

  it('parses fields parameter', () => {
    const result = parseTableParams({ fields: 'id,name,email' });
    expect(result.fields).toEqual(['id', 'name', 'email']);
  });

  it('trims whitespace from field names', () => {
    const result = parseTableParams({ fields: ' id , name ' });
    expect(result.fields).toEqual(['id', 'name']);
  });

  it('ignores empty field names', () => {
    const result = parseTableParams({ fields: ',id,,name,' });
    expect(result.fields).toEqual(['id', 'name']);
  });

  it('handles array-valued search params (takes first)', () => {
    const result = parseTableParams({ sort: ['name', '-id'] });
    expect(result.sort).toEqual([{ field: 'name', direction: 'asc' }]);
  });

  it('combines all parameter types', () => {
    const result = parseTableParams({
      sort: '-created_at',
      limit: '10',
      offset: '20',
      'filter[status]': 'active',
      fields: 'id,name',
    });
    expect(result).toEqual({
      sort: [{ field: 'created_at', direction: 'desc' }],
      pagination: { limit: 10, offset: 20 },
      filter: { status: 'active' },
      fields: ['id', 'name'],
    });
  });

  it('parses null literal as filter value', () => {
    const result = parseTableParams({ 'filter[deleted_at][eq]': 'null' });
    expect(result.filter).toEqual({ deleted_at: { _eq: null } });
  });
});
