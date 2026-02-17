import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useContext } from 'react';
import { BifrostContext } from '../components/bifrost-provider';
import { executeGraphQL } from '../utils/graphql-client';

/** Options for the {@link useBifrostMutation} hook. */
export interface UseBifrostMutationOptions {
  /**
   * Query keys to invalidate on success. Each string matches against the
   * second element of the `['bifrost', query]` key pattern.
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
      ),
    onSuccess: (data) => {
      if (options.invalidateQueries) {
        for (const key of options.invalidateQueries) {
          queryClient.invalidateQueries({ queryKey: ['bifrost', key] });
        }
      }
      options.onSuccess?.(data);
    },
    onError: options.onError,
  });
}
