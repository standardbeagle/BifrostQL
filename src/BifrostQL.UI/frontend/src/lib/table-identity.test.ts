import { describe, expect, it } from 'vitest';
import { findBuilderTable, qualifiedDbName } from './table-identity';

// Two schemas share the bare name `users`; dbo is listed first so a first-match
// lookup lands on it.
const builderTables = [
  { schema: 'dbo', name: 'users', qualified: 'dbo.users' },
  { schema: 'sales', name: 'users', qualified: 'sales.users' },
  { schema: 'dbo', name: 'orders', qualified: 'dbo.orders' },
];

describe('qualifiedDbName', () => {
  it('qualifies the bare dbName with its schema', () => {
    expect(qualifiedDbName({ schema: 'sales', dbName: 'users' })).toBe('sales.users');
    expect(qualifiedDbName({ schema: 'dbo', dbName: 'users' })).toBe('dbo.users');
  });

  it('falls back to the bare dbName when the schema is unknown', () => {
    expect(qualifiedDbName({ dbName: 'users' })).toBe('users');
    expect(qualifiedDbName({ schema: '', dbName: 'users' })).toBe('users');
  });
});

describe('findBuilderTable', () => {
  it('resolves a qualified name to that schema’s table', () => {
    expect(findBuilderTable(builderTables, 'sales.users')?.schema).toBe('sales');
    expect(findBuilderTable(builderTables, 'DBO.USERS')?.schema).toBe('dbo');
  });

  it('resolves a bare name only one schema defines', () => {
    expect(findBuilderTable(builderTables, 'orders')?.qualified).toBe('dbo.orders');
  });

  it('resolves a bare name two schemas share to nothing, like an unknown name', () => {
    expect(findBuilderTable(builderTables, 'users')).toBeUndefined();
    expect(findBuilderTable(builderTables, 'no_such_table')).toBeUndefined();
  });

  it('round-trips every editor table through qualifiedDbName', () => {
    for (const t of builderTables) {
      expect(findBuilderTable(builderTables, qualifiedDbName({ schema: t.schema, dbName: t.name }))).toBe(t);
    }
  });
});
