import { describe, it, expectTypeOf } from 'vitest';
import type { FieldNameOf, SortOption, SortOptionFor } from './query';

interface User {
  id: number;
  name: string;
  email: string;
}

/**
 * Type-level contract for typed field names: a concrete row type constrains
 * `FieldNameOf` / `SortOptionFor` to that row's keys, the untyped fallbacks
 * stay plain strings, and the typed shapes remain structurally assignable to
 * the untyped query contract.
 */
describe('typed field names', () => {
  it('resolves FieldNameOf to row keys for a concrete row type', () => {
    expectTypeOf<FieldNameOf<User>>().toEqualTypeOf<'id' | 'name' | 'email'>();
  });

  it('falls back to string for untyped and index-signature rows', () => {
    expectTypeOf<FieldNameOf<unknown>>().toEqualTypeOf<string>();
    expectTypeOf<
      FieldNameOf<Record<string, unknown>>
    >().toEqualTypeOf<string>();
  });

  it('constrains SortOptionFor to the row keys', () => {
    const good: SortOptionFor<User> = { field: 'email', direction: 'asc' };
    expectTypeOf(good).toMatchTypeOf<SortOptionFor<User>>();

    const bad: SortOptionFor<User> = {
      // @ts-expect-error -- 'created_at' is not a key of User
      field: 'created_at',
      direction: 'desc',
    };
    void bad;
  });

  it('keeps SortOptionFor assignable to the untyped SortOption contract', () => {
    const typed: SortOptionFor<User> = { field: 'name', direction: 'asc' };
    const untyped: SortOption = typed;
    expectTypeOf(untyped).toMatchTypeOf<SortOption>();
  });
});
