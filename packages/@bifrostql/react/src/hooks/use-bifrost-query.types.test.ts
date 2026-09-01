import { describe, it, expect } from 'vitest';
import type { RowOf, UseBifrostQueryOptions } from './use-bifrost-query';
import type { UseBifrostTableOptions } from './use-bifrost-table.types';

interface User {
  id: number;
  name: string;
  email: string;
}

/**
 * Compile-time contract for typed field selection on the hook option
 * surfaces. The `@ts-expect-error` lines are enforced by `tsc --noEmit`
 * (the `typecheck` script and the build's `tsc` step): if the generics are
 * loosened back to `string[]`, each directive becomes "unused" and the
 * typecheck fails.
 */
describe('typed field selection', () => {
  it('unwraps the row type from an array result type', () => {
    const rowChecks: [RowOf<User[]>, RowOf<unknown>] = [
      { id: 1, name: 'a', email: 'b' },
      undefined,
    ];
    expect(rowChecks).toHaveLength(2);
  });

  it('constrains useBifrostQuery options to the row keys', () => {
    const good: UseBifrostQueryOptions<User> = {
      fields: ['id', 'name'],
      sort: [{ field: 'email', direction: 'asc' }],
    };

    const badField: UseBifrostQueryOptions<User> = {
      // @ts-expect-error -- 'nope' is not a key of User
      fields: ['id', 'nope'],
    };

    const badSort: UseBifrostQueryOptions<User> = {
      // @ts-expect-error -- 'created_at' is not a key of User
      sort: [{ field: 'created_at', direction: 'desc' }],
    };

    expect([good, badField, badSort]).toHaveLength(3);
  });

  it('constrains useBifrostTable fields and defaultSort to the row keys', () => {
    const good: Pick<UseBifrostTableOptions<User>, 'fields' | 'defaultSort'> = {
      fields: ['id', 'email'],
      defaultSort: [{ field: 'name', direction: 'asc' }],
    };

    const bad: Pick<UseBifrostTableOptions<User>, 'fields'> = {
      // @ts-expect-error -- 'nope' is not a key of User
      fields: ['nope'],
    };

    expect([good, bad]).toHaveLength(2);
  });

  it('keeps untyped usage source-compatible with plain string arrays', () => {
    const untypedQuery: UseBifrostQueryOptions = {
      fields: ['anything'],
      sort: [{ field: 'whatever', direction: 'desc' }],
    };
    const untypedTable: Pick<UseBifrostTableOptions, 'fields' | 'defaultSort'> =
      {
        fields: ['anything'],
        defaultSort: [{ field: 'whatever', direction: 'desc' }],
      };
    expect([untypedQuery, untypedTable]).toHaveLength(2);
  });
});
