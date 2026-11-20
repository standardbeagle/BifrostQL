import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useCallback, useContext, useEffect, useRef } from 'react';
import {
  BifrostContext,
  BifrostMutationContext,
} from '../components/bifrost-provider';
import { executeGraphQL, isGraphQLAnswer } from '../utils/graphql-client';
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
 * Every call sends an `Idempotency-Key` header, so a server that records operation ids applies
 * one intent once. A call sends a new `crypto.randomUUID()`, unless it is a retry: this
 * instance's next call after its last call ended unanswered (the fetch failed or the status was
 * not 2xx), whatever its variables. A retry repeats the key; the server answers an unchanged
 * retry with the first result, runs a changed one when the first rolled back, and refuses it
 * with `OPERATION_ID_REUSED` when the first applied. The provider forgets the unanswered call,
 * and clears this instance's `error`, on any GraphQL answer to it, a call sent through the
 * provider by another instance, `reset()`, or unmount.
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

  const store = useContext(BifrostMutationContext);
  if (!store) {
    throw new Error('useBifrostMutation must be used within a BifrostProvider');
  }

  const queryClient = useQueryClient();
  const owner = useRef(Symbol('useBifrostMutation')).current;
  const resetRef = useRef<() => void>(() => undefined);

  const forgetOwn = useCallback(() => {
    if (store.pending?.owner === owner) {
      store.pending = null;
    }
  }, [store, owner]);

  useEffect(() => forgetOwn, [forgetOwn]);

  const result = useMutation<TData, Error, TVariables>({
    mutationFn: async (variables) => {
      const pending = store.pending;
      const isRetry = pending?.owner === owner;
      if (pending && !isRetry) {
        // Another instance's unanswered call: this call ends its life as a retry.
        store.pending = null;
        pending.forget();
      }
      const key = isRetry ? pending.key : crypto.randomUUID();
      try {
        const data = await executeGraphQL<TData>(
          config.endpoint,
          { ...config.headers, 'Idempotency-Key': key },
          mutation,
          variables,
          undefined,
          config.getToken,
          {
            refreshToken: config.refreshToken,
            onSessionExpired: config.onSessionExpired,
          },
        );
        forgetOwn();
        return data;
      } catch (error) {
        if (isGraphQLAnswer(error)) {
          forgetOwn();
        } else {
          store.pending = { owner, key, forget: () => resetRef.current() };
        }
        throw error;
      }
    },
    onSuccess: (data) => {
      if (options.invalidateQueries) {
        invalidateBifrostQueries(
          queryClient,
          config.endpoint,
          options.invalidateQueries,
        );
      }
      options.onSuccess?.(data);
    },
    onError: options.onError,
  });
  resetRef.current = result.reset;

  const reset = useCallback(() => {
    forgetOwn();
    result.reset();
  }, [forgetOwn, result]);

  return { ...result, reset };
}
