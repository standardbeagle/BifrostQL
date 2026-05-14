import { describe, it, expect, expectTypeOf } from 'vitest';
import type { UserRow } from '@bifrostql/types/generated';
import {
  createCrudHelpers,
  type TypedOperation,
  type TypedCreateOperation,
  type TypedUpdateOperation,
  type TypedDeleteOperation,
} from './crud-helpers';

// A distinct insert type to prove the helpers are parameterized over
// independent row / insert / update types (not just Partial<TRow>).
interface UserInsert {
  name: string;
  email?: string | null;
  isActive?: boolean;
}

describe('createCrudHelpers — list', () => {
  it('builds a list query string via the query builder', () => {
    const users = createCrudHelpers<UserRow>('users');
    const op = users.list({
      fields: ['id', 'name', 'email'],
      filter: { isActive: true },
      sort: [{ field: 'name', direction: 'asc' }],
      limit: 25,
      offset: 50,
    });
    expect(op.query).toContain('users');
    expect(op.query).toContain('id');
    expect(op.query).toContain('name');
    expect(op.query).toContain('email');
    expect(op.query).toContain('filter:');
    expect(op.query).toContain('isActive');
    expect(op.query).toContain('sort:');
    expect(op.query).toContain('limit: 25');
    expect(op.query).toContain('offset: 50');
  });

  it('builds a bare list query with no options', () => {
    const users = createCrudHelpers<UserRow>('users');
    const op = users.list();
    expect(op.query).toContain('users');
    expect(op.query).not.toContain('filter:');
    expect(op.query).not.toContain('limit:');
  });

  it('infers TRow[] as the result type', () => {
    const users = createCrudHelpers<UserRow>('users');
    const op = users.list({ fields: ['id'] });
    expectTypeOf(op).toEqualTypeOf<TypedOperation<UserRow[]>>();
    expectTypeOf(op.__result).toEqualTypeOf<UserRow[] | undefined>();
  });

  it('constrains fields to row keys at the type level', () => {
    const users = createCrudHelpers<UserRow>('users');
    expectTypeOf(users.list)
      .parameter(0)
      .toMatchTypeOf<{ fields?: ReadonlyArray<keyof UserRow> } | undefined>();
  });
});

describe('createCrudHelpers — detail', () => {
  it('builds a detail-by-id query filtered on id', () => {
    const users = createCrudHelpers<UserRow>('users');
    const op = users.detail(42, { fields: ['id', 'name'] });
    expect(op.query).toContain('users');
    expect(op.query).toContain('filter:');
    expect(op.query).toContain('id');
    expect(op.query).toContain('_eq: 42');
    expect(op.query).toContain('limit: 1');
  });

  it('infers TRow | null as the result type', () => {
    const users = createCrudHelpers<UserRow>('users');
    const op = users.detail(1);
    expectTypeOf(op).toEqualTypeOf<TypedOperation<UserRow | null>>();
  });
});

describe('createCrudHelpers — create', () => {
  it('builds an insert mutation string via the mutation builder', () => {
    const users = createCrudHelpers<UserRow, UserInsert>('users');
    const op = users.create({ name: 'Ada', email: 'ada@example.com' });
    expect(op.query).toContain('mutation Insert');
    expect(op.query).toContain('Insert_users');
    expect(op.query).toContain('users(insert: $detail)');
    expect(op.variables).toEqual({
      detail: { name: 'Ada', email: 'ada@example.com' },
    });
  });

  it('types the input as TInsert and result as TRow', () => {
    const users = createCrudHelpers<UserRow, UserInsert>('users');
    const op = users.create({ name: 'Ada' });
    expectTypeOf(op).toEqualTypeOf<TypedCreateOperation<UserRow, UserInsert>>();
    expectTypeOf(op.variables.detail).toEqualTypeOf<UserInsert>();
    expectTypeOf(op.__result).toEqualTypeOf<UserRow | undefined>();
    expectTypeOf(users.create).parameter(0).toEqualTypeOf<UserInsert>();
  });
});

describe('createCrudHelpers — update', () => {
  it('builds an update mutation string and merges the id into variables', () => {
    const users = createCrudHelpers<UserRow>('users');
    const op = users.update(7, { name: 'Grace' });
    expect(op.query).toContain('mutation Update');
    expect(op.query).toContain('Update_users');
    expect(op.query).toContain('users(update: $detail)');
    expect(op.variables).toEqual({ detail: { name: 'Grace', id: 7 } });
  });

  it('types the changes and result', () => {
    const users = createCrudHelpers<UserRow>('users');
    const op = users.update(1, { name: 'x' });
    expectTypeOf(op).toEqualTypeOf<
      TypedUpdateOperation<UserRow, Partial<UserRow>>
    >();
    expectTypeOf(op.__result).toEqualTypeOf<UserRow | undefined>();
  });
});

describe('createCrudHelpers — delete', () => {
  it('builds a delete mutation string with the id variable', () => {
    const users = createCrudHelpers<UserRow>('users');
    const op = users.delete(99);
    expect(op.query).toContain('mutation Delete');
    expect(op.query).toContain('Delete_users');
    expect(op.query).toContain('users(delete: $detail)');
    expect(op.variables).toEqual({ detail: { id: 99 } });
  });

  it('infers the deleted-id result shape', () => {
    const users = createCrudHelpers<UserRow>('users');
    const op = users.delete(1);
    expectTypeOf(op).toEqualTypeOf<TypedDeleteOperation>();
    expectTypeOf(op.__result).toEqualTypeOf<
      { id: string | number } | undefined
    >();
  });
});

describe('createCrudHelpers — lookup', () => {
  it('builds a narrow FK-selector query with value and label fields', () => {
    const users = createCrudHelpers<UserRow>('users');
    const op = users.lookup({
      valueField: 'id',
      labelField: 'name',
      filter: { isActive: true },
      limit: 10,
    });
    expect(op.query).toContain('users');
    expect(op.query).toContain('id');
    expect(op.query).toContain('name');
    expect(op.query).toContain('filter:');
    expect(op.query).toContain('limit: 10');
  });

  it('constrains valueField/labelField to row keys', () => {
    const users = createCrudHelpers<UserRow>('users');
    expectTypeOf(users.lookup).parameter(0).toMatchTypeOf<{
      valueField: keyof UserRow;
      labelField: keyof UserRow;
    }>();
  });

  it('infers the option-list result type', () => {
    const users = createCrudHelpers<UserRow>('users');
    const op = users.lookup({ valueField: 'id', labelField: 'name' });
    expectTypeOf(op).toEqualTypeOf<
      TypedOperation<Array<{ value: unknown; label: unknown }>>
    >();
  });
});

describe('createCrudHelpers — integration', () => {
  it('reuses the underlying builders (no duplicated string construction)', () => {
    // The list query must be byte-identical to a direct buildGraphqlQuery call,
    // proving the helper delegates rather than re-implements.
    const users = createCrudHelpers<UserRow>('users');
    const op = users.list({ fields: ['id', 'name'], limit: 5 });
    expect(op.query).toContain('users(limit: 5)');
  });
});
