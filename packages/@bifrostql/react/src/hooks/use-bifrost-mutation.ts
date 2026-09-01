import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useContext } from 'react';
import { BifrostContext } from '../components/bifrost-provider';
import { executeGraphQL } from '../utils/graphql-client';
import { invalidateBifrostQueries } from '../utils/invalidation';

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
        invalidateBifrostQueries(queryClient, options.invalidateQueries);
      }
      options.onSuccess?.(data);
    },
    onError: options.onError,
  });
}
