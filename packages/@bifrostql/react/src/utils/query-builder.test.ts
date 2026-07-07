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

  it('translates _nnull to _null: false (schema has no _nnull)', () => {
    const result = buildGraphqlQuery('users', {
      filter: { email: { _nnull: true } },
      fields: ['id'],
    });
    expect(result).toContain('_null: false');
    expect(result).not.toContain('_nnull');
  });

  it('builds a query with sort options', () => {
    const result = buildGraphqlQuery('users', {
      sort: [{ field: 'name', direction: 'asc' }],
      fields: ['id', 'name'],
    });
    expect(result).toContain('sort:');
    expect(result).toContain('name_asc');
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

  it('serializes a public _and compound filter as the server and field', () => {
    const result = buildGraphqlQuery('users', {
      filter: {
        _and: [
          { status: { _eq: 'active' } },
          { created_at: { _gte: '2024-01-01' } },
        ],
      },
      fields: ['id', 'name'],
    });
    expect(result).toContain('and:');
    expect(result).not.toContain('_and');
    expect(result).toContain('_eq');
    expect(result).toContain('_gte');
  });

  it('serializes a public _or compound filter as the server or field', () => {
    const result = buildGraphqlQuery('users', {
      filter: {
        _or: [{ name: { _eq: 'Alice' } }, { name: { _eq: 'Bob' } }],
      },
      fields: ['id', 'name'],
    });
    expect(result).toContain('or:');
    expect(result).not.toContain('_or');
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
    expect(result).toContain('and:');
    expect(result).toContain('or:');
    expect(result).not.toContain('_and');
    expect(result).not.toContain('_or');
    expect(result).toContain('"active"');
    expect(result).toContain('"admin"');
    expect(result).toContain('"editor"');
  });

  it('rejects unsafe table names', () => {
    expect(() => buildGraphqlQuery('users) { injected')).toThrow(
      /Invalid GraphQL table/,
    );
  });

  it('rejects unsafe field names', () => {
    expect(() =>
      buildGraphqlQuery('users', {
        fields: ['id', 'name) { injected'],
      }),
    ).toThrow(/Invalid GraphQL selection field/);
  });

  it('rejects unsafe filter fields and operators', () => {
    expect(() =>
      buildGraphqlQuery('users', {
        filter: { 'status) { injected': 'active' },
      }),
    ).toThrow(/Invalid GraphQL filter field/);

    expect(() =>
      buildGraphqlQuery('users', {
        filter: {
          // @ts-expect-error runtime validation rejects unknown operators too
          status: { _containsInjected: 'active' },
        },
      }),
    ).toThrow(/Invalid GraphQL filter operator/);
  });

  it('rejects unsafe sort fields', () => {
    expect(() =>
      buildGraphqlQuery('users', {
        sort: [{ field: 'name) { injected', direction: 'asc' }],
      }),
    ).toThrow(/Invalid GraphQL sort field/);
  });

  it('rejects unsafe sort directions at runtime', () => {
    expect(() =>
      buildGraphqlQuery('users', {
        sort: [
          {
            field: 'name',
            // @ts-expect-error runtime validation protects JS callers too
            direction: 'asc] } injected',
          },
        ],
      }),
    ).toThrow(/Invalid GraphQL sort direction/);
  });

  it('rejects invalid pagination values at runtime', () => {
    expect(() =>
      buildGraphqlQuery('users', {
        pagination: { limit: 10.5 },
      }),
    ).toThrow(/Invalid GraphQL pagination limit/);

    expect(() =>
      buildGraphqlQuery('users', {
        pagination: { offset: -1 },
      }),
    ).toThrow(/Invalid GraphQL pagination offset/);

    expect(() =>
      buildGraphqlQuery('users', {
        pagination: { limit: Number.POSITIVE_INFINITY },
      }),
    ).toThrow(/Invalid GraphQL pagination limit/);
  });
});
