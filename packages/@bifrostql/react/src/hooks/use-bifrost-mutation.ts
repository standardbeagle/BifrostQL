import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useContext } from 'react';
import { BifrostContext } from '../components/bifrost-provider';
import { executeGraphQL } from '../utils/graphql-client';

/** Options for the {@link useBifrostMutation} hook. */
export interface UseBifrostMutationOptions {
  /**
   * Queries to invalidate on success. Bifrost queries are keyed
   * `['bifrost', <full query string>, vars]`, so each entry is matched one
   * of two ways: an entry containing `{` is treated as a full query string
   * and matched as an exact key prefix; anything else is treated as a TABLE
   * NAME and invalidates every bifrost query whose query text references it
   * as a word (`['users']` invalidates each cached query that reads `users`).
   * Table-name matching may over-invalidate (a table name appearing in an
   * unrelated query's text) — over-invalidation only costs a refetch.
   */
  invalidateQueries?: string[];
  /** Callback invoked when the mutation succeeds. */
  onSuccess?: (data: unknown) => void;
  /** Callback invoked when the mutation fails. */
  onError?: (error: Error) => void;
}

/**
 * Hook for executing GraphQL mutations with automatic query invalidation.
 *
 * Use the `buildInsertMutation`, `buildUpdateMutation`, `buildUpsertMutation`,
 * or `buildDeleteMutation` helpers to construct the mutation string.
 *
 * Must be used within a {@link BifrostProvider}.
 *
 * @typeParam TData - The expected mutation response type.
 * @typeParam TVariables - The mutation variables type.
 * @param mutation - A GraphQL mutation string.
 * @param options - Invalidation, success, and error callbacks.
 * @returns A TanStack Query mutation result.
 *
 * @example
 * ```tsx
 * const { mutate } = useBifrostMutation<User, { detail: Partial<User> }>(
 *   buildInsertMutation('users'),
 *   { invalidateQueries: ['users'] },
 * );
 * mutate({ detail: { name: 'Alice', email: 'alice@example.com' } });
 * ```
 */
export function useBifrostMutation<
  TData = unknown,
  TVariables extends Record<string, unknown> = Record<string, unknown>,
>(mutation: string, options: UseBifrostMutationOptions = {}) {
  const config = useContext(BifrostContext);
  if (!config) {
    throw new Error('useBifrostMutation must be used within a BifrostProvider');
  }

  const queryClient = useQueryClient();

  return useMutation<TData, Error, TVariables>({
    mutationFn: (variables) =>
      executeGraphQL<TData>(
        config.endpoint,
        config.headers ?? {},
        mutation,
        variables,
        undefined,
        config.getToken,
        {
          refreshToken: config.refreshToken,
          onSessionExpired: config.onSessionExpired,
        },
      ),
    onSuccess: (data) => {
      if (options.invalidateQueries) {
        for (const key of options.invalidateQueries) {
          if (key.includes('{')) {
            // Full query string: exact key-prefix match, as before.
            queryClient.invalidateQueries({ queryKey: ['bifrost', key] });
          } else {
            // Table name: the cache keys hold full query strings, so a
            // bare name used as a key prefix matches nothing. Match any
            // bifrost query whose text references the table as a word.
            const word = new RegExp(
              `\\b${key.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\b`,
            );
            queryClient.invalidateQueries({
              predicate: (q) =>
                q.queryKey[0] === 'bifrost' &&
                typeof q.queryKey[1] === 'string' &&
                word.test(q.queryKey[1]),
            });
          }
        }
      }
      options.onSuccess?.(data);
    },
    onError: options.onError,
  });
}
