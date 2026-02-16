import { describe, it, expect } from 'vitest';
import { buildGraphqlQuery } from './query-builder';

describe('buildGraphqlQuery', () => {
  it('builds a basic query with no options', () => {
    const result = buildGraphqlQuery('users');
    expect(result).toContain('users');
    expect(result).toContain('__typename');
  });

  it('builds a query with specified fields', () => {
    const result = buildGraphqlQuery('users', {
      fields: ['id', 'name', 'email'],
    });
    expect(result).toContain('id');
    expect(result).toContain('name');
    expect(result).toContain('email');
    expect(result).not.toContain('__typename');
  });

  it('builds a query with equality filter', () => {
    const result = buildGraphqlQuery('users', {
      filter: { status: 'active' },
      fields: ['id'],
    });
    expect(result).toContain('filter:');
    expect(result).toContain('status');
    expect(result).toContain('_eq');
    expect(result).toContain('"active"');
  });

  it('builds a query with field filter operators', () => {
    const result = buildGraphqlQuery('orders', {
      filter: { total: { _gte: 100, _lt: 500 } },
      fields: ['id', 'total'],
    });
    expect(result).toContain('_gte: 100');
    expect(result).toContain('_lt: 500');
  });

  it('builds a query with null filter', () => {
    const result = buildGraphqlQuery('users', {
      filter: { deleted_at: null },
      fields: ['id'],
    });
    expect(result).toContain('_null: true');
  });

  it('builds a query with sort options', () => {
    const result = buildGraphqlQuery('users', {
      sort: [{ field: 'name', direction: 'asc' }],
      fields: ['id', 'name'],
    });
    expect(result).toContain('sort:');
    expect(result).toContain('name asc');
  });

  it('builds a query with pagination', () => {
    const result = buildGraphqlQuery('users', {
      pagination: { limit: 10, offset: 20 },
      fields: ['id'],
    });
    expect(result).toContain('limit: 10');
    expect(result).toContain('offset: 20');
  });

  it('combines filter, sort, and pagination', () => {
    const result = buildGraphqlQuery('users', {
      filter: { active: true },
      sort: [{ field: 'created_at', direction: 'desc' }],
      pagination: { limit: 25 },
      fields: ['id', 'name'],
    });
    expect(result).toContain('filter:');
    expect(result).toContain('sort:');
    expect(result).toContain('limit: 25');
    expect(result).toContain('id');
    expect(result).toContain('name');
  });

  it('builds a query with _between filter (expands to _gte + _lte)', () => {
    const result = buildGraphqlQuery('orders', {
      filter: { amount: { _between: [100, 500] } },
      fields: ['id', 'amount'],
    });
    expect(result).toContain('_gte: 100');
    expect(result).toContain('_lte: 500');
    expect(result).not.toContain('_between');
  });

  it('builds a query with _and compound filter', () => {
    const result = buildGraphqlQuery('users', {
      filter: {
        _and: [
          { status: { _eq: 'active' } },
          { created_at: { _gte: '2024-01-01' } },
        ],
      },
      fields: ['id', 'name'],
    });
    expect(result).toContain('_and');
    expect(result).toContain('_eq');
    expect(result).toContain('_gte');
  });

  it('builds a query with _or compound filter', () => {
    const result = buildGraphqlQuery('users', {
      filter: {
        _or: [{ name: { _eq: 'Alice' } }, { name: { _eq: 'Bob' } }],
      },
      fields: ['id', 'name'],
    });
    expect(result).toContain('_or');
    expect(result).toContain('"Alice"');
    expect(result).toContain('"Bob"');
  });

  it('builds a query with nested compound filters', () => {
    const result = buildGraphqlQuery('users', {
      filter: {
        _and: [
          { status: { _eq: 'active' } },
          {
            _or: [{ role: { _eq: 'admin' } }, { role: { _eq: 'editor' } }],
          },
        ],
      },
      fields: ['id'],
    });
    expect(result).toContain('_and');
    expect(result).toContain('_or');
    expect(result).toContain('"active"');
    expect(result).toContain('"admin"');
    expect(result).toContain('"editor"');
  });
});
