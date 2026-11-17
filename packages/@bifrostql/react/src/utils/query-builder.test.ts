import { describe, it, expect } from 'vitest';
import { buildGraphqlQuery } from './query-builder';

describe('buildGraphqlQuery', () => {
  it('builds a total-only paged query with no options', () => {
    const { query, variables } = buildGraphqlQuery('users');
    expect(query).toContain('users');
    expect(query).toContain('total');
    expect(query).not.toContain('data');
    expect(variables).toEqual({});
  });

  it('emits the paged data{} envelope around the field selection', () => {
    // BifrostQL types every top-level table field as the paged envelope
    // (data/total/offset/limit); a flat selection is rejected by the server.
    const { query } = buildGraphqlQuery('users', { fields: ['id', 'name'] });
    expect(query.replace(/\s+/g, ' ')).toBe(
      '{ users { total data { id name } } }',
    );
  });

  it('builds a query with specified fields', () => {
    const { query } = buildGraphqlQuery('users', {
      fields: ['id', 'name', 'email'],
    });
    expect(query).toContain('id');
    expect(query).toContain('name');
    expect(query).toContain('email');
    expect(query).not.toContain('__typename');
    expect(query).toContain('data {');
  });

  it('sends filter values as a typed $filter variable, never as document text', () => {
    // Arrange: a value that would change the document if it were inlined.
    const hostile = 'x" } } injected { "';

    // Act
    const { query, variables } = buildGraphqlQuery('users', {
      filter: { status: hostile },
      fields: ['id'],
    });

    // Assert: the document declares and passes the variable, the value is
    // only in the variables.
    expect(query.replace(/\s+/g, ' ')).toBe(
      'query ($filter: TableFilterusersInput) { users(filter: $filter) { total data { id } } }',
    );
    expect(query).not.toContain('injected');
    expect(variables).toEqual({ filter: { status: { _eq: hostile } } });
  });

  it('reuses one document for different filter values', () => {
    const a = buildGraphqlQuery('users', {
      filter: { status: 'active' },
      fields: ['id'],
    });
    const b = buildGraphqlQuery('users', {
      filter: { status: 'banned' },
      fields: ['id'],
    });
    expect(a.query).toBe(b.query);
    expect(a.variables).not.toEqual(b.variables);
  });

  it('passes field filter operators through', () => {
    const { variables } = buildGraphqlQuery('orders', {
      filter: { total: { _gte: 100, _lt: 500 } },
      fields: ['id', 'total'],
    });
    expect(variables).toEqual({ filter: { total: { _gte: 100, _lt: 500 } } });
  });

  it('maps a null filter value to _null: true', () => {
    const { variables } = buildGraphqlQuery('users', {
      filter: { deleted_at: null },
      fields: ['id'],
    });
    expect(variables).toEqual({ filter: { deleted_at: { _null: true } } });
  });

  it('translates _nnull to _null: false (schema has no _nnull)', () => {
    const { variables } = buildGraphqlQuery('users', {
      filter: { email: { _nnull: true } },
      fields: ['id'],
    });
    expect(variables).toEqual({ filter: { email: { _null: false } } });
  });

  it('omits the variable for an empty filter', () => {
    const { query, variables } = buildGraphqlQuery('users', {
      filter: {},
      fields: ['id'],
    });
    expect(query).not.toContain('$filter');
    expect(variables).toEqual({});
  });

  it('builds a query with sort options', () => {
    const { query } = buildGraphqlQuery('users', {
      sort: [{ field: 'name', direction: 'asc' }],
      fields: ['id', 'name'],
    });
    expect(query).toContain('sort:');
    expect(query).toContain('name_asc');
  });

  it('builds a query with pagination', () => {
    const { query } = buildGraphqlQuery('users', {
      pagination: { limit: 10, offset: 20 },
      fields: ['id'],
    });
    expect(query).toContain('limit: 10');
    expect(query).toContain('offset: 20');
  });

  it('combines filter, sort, and pagination', () => {
    const { query, variables } = buildGraphqlQuery('users', {
      filter: { active: true },
      sort: [{ field: 'created_at', direction: 'desc' }],
      pagination: { limit: 25 },
      fields: ['id', 'name'],
    });
    expect(query.replace(/\s+/g, ' ')).toContain(
      'users(filter: $filter, sort: [created_at_desc], limit: 25)',
    );
    expect(variables).toEqual({ filter: { active: { _eq: true } } });
  });

  it('expands a _between filter to _gte + _lte', () => {
    const { variables } = buildGraphqlQuery('orders', {
      filter: { amount: { _between: [100, 500] } },
      fields: ['id', 'amount'],
    });
    expect(variables).toEqual({
      filter: { amount: { _gte: 100, _lte: 500 } },
    });
  });

  it('serializes a public _and compound filter as the server and field', () => {
    const { variables } = buildGraphqlQuery('users', {
      filter: {
        _and: [
          { status: { _eq: 'active' } },
          { created_at: { _gte: '2024-01-01' } },
        ],
      },
      fields: ['id', 'name'],
    });
    expect(variables).toEqual({
      filter: {
        and: [
          { status: { _eq: 'active' } },
          { created_at: { _gte: '2024-01-01' } },
        ],
      },
    });
  });

  it('serializes nested compound filters', () => {
    const { variables } = buildGraphqlQuery('users', {
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
    expect(variables).toEqual({
      filter: {
        and: [
          { status: { _eq: 'active' } },
          { or: [{ role: { _eq: 'admin' } }, { role: { _eq: 'editor' } }] },
        ],
      },
    });
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
