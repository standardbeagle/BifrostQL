import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useContext, useRef, useCallback } from 'react';
import { BifrostContext } from '../components/bifrost-provider';
import { executeGraphQL } from '../utils/graphql-client';
import { buildUpdateMutation } from '../utils/mutation-builder';
import { diff, detectConflicts } from '../utils/diff-engine';
import type { DiffStrategy, DiffResult } from '../utils/diff-engine';

/** Options for the {@link useBifrostDiff} hook. */
export interface UseBifrostDiffOptions {
  /** The database table name to update. */
  table: string;
  /** The primary key field name (used to identify the row). */
  idField: string;
  /** Comparison strategy: `'shallow'` or `'deep'` (default). */
  strategy?: DiffStrategy;
  /** Query keys to invalidate on success. */
  invalidateQueries?: string[];
  /** Callback invoked when the mutation succeeds. */
  onSuccess?: (data: unknown) => void;
  /** Callback invoked when the mutation fails. */
  onError?: (error: Error) => void;
}

/** Input for a diff-based mutation, providing original and updated row states. */
export interface DiffMutationInput {
  /** The row's primary key value. */
  id: string | number;
  /** The original (baseline) row data fetched from the server. */
  original: Record<string, unknown>;
  /** The locally modified row data. */
  updated: Record<string, unknown>;
}

/** Result of a diff preview, showing what would change and any conflicts. */
export interface DiffMutationResult {
  /** The computed diff between original and updated. */
  diff: DiffResult;
  /** Field names where the server state conflicts with local changes. */
  conflicts: string[];
}

/**
 * Hook for diff-based mutations that send only changed fields to the server.
 *
 * Computes the difference between original and updated row data, optionally
 * detects three-way merge conflicts against a last-known server state, and
 * submits only the changed fields as an update mutation.
 *
 * Returns a TanStack Query mutation plus:
 * - `preview(input)` - Preview the diff and conflicts without submitting.
 * - `setLastKnown(state)` - Store the last-known server state for conflict detection.
 *
 * Must be used within a {@link BifrostProvider}.
 *
 * @param options - Configuration including table name, ID field, and diff strategy.
 * @returns Mutation result with `preview` and `setLastKnown` methods.
 *
 * @example
 * ```tsx
 * const { mutate, preview, setLastKnown } = useBifrostDiff({
 *   table: 'users',
 *   idField: 'id',
 *   strategy: 'deep',
 *   invalidateQueries: ['users'],
 * });
 *
 * setLastKnown(serverRow);
 * const { diff, conflicts } = preview({ id: 1, original: serverRow, updated: editedRow });
 * if (conflicts.length === 0) {
 *   mutate({ id: 1, original: serverRow, updated: editedRow });
 * }
 * ```
 */
export function useBifrostDiff(options: UseBifrostDiffOptions) {
  const config = useContext(BifrostContext);
  if (!config) {
    throw new Error('useBifrostDiff must be used within a BifrostProvider');
  }

  const { table, idField, strategy = 'deep' } = options;
  const queryClient = useQueryClient();
  const lastKnownRef = useRef<Record<string, unknown> | null>(null);

  const mutation = useMutation<unknown, Error, DiffMutationInput>({
    mutationFn: (input) => {
      const { id, original, updated } = input;
      const result = diff(original, updated, strategy);

      if (!result.hasChanges) {
        return Promise.resolve(null);
      }

      if (lastKnownRef.current) {
        const conflicts = detectConflicts(
          original,
          lastKnownRef.current,
          updated,
        );
        if (conflicts.length > 0) {
          return Promise.reject(
            new Error(`Conflict detected on fields: ${conflicts.join(', ')}`),
          );
        }
      }

      const detail = { [idField]: id, ...result.changed };
      const gql = buildUpdateMutation(table);

      return executeGraphQL(
        config.endpoint,
        config.headers ?? {},
        gql,
        { detail },
        undefined,
        config.getToken,
      );
    },
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

  const preview = useCallback(
    (input: DiffMutationInput): DiffMutationResult => {
      const result = diff(input.original, input.updated, strategy);
      const conflicts = lastKnownRef.current
        ? detectConflicts(input.original, lastKnownRef.current, input.updated)
        : [];
      return { diff: result, conflicts };
    },
    [strategy],
  );

  const setLastKnown = useCallback((state: Record<string, unknown>) => {
    lastKnownRef.current = state;
  }, []);

  return {
    ...mutation,
    preview,
    setLastKnown,
  };
}
