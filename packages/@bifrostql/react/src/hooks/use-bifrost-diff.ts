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

/**
 * Bucket for a last-known state stored without an explicit row id via
 * `setLastKnown(state)`. Acts as a fallback for single-row callers; keyed
 * entries (`setLastKnown(state, id)`) take precedence for their row.
 */
const DEFAULT_LAST_KNOWN_KEY = '__default__';

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
  // Last-known server state keyed by row id, so conflict detection compares the
  // row being saved against *that same row's* last-known state — not a single
  // hook-wide snapshot, which caused cross-row false conflicts when the same
  // hook instance saved multiple rows. Entries stored without an explicit id
  // land under DEFAULT_LAST_KNOWN_KEY and act as a fallback for callers that
  // track a single row.
  const lastKnownRef = useRef<Map<string, Record<string, unknown>>>(new Map());

  const resolveLastKnown = useCallback(
    (id: string | number): Record<string, unknown> | null => {
      const byId = lastKnownRef.current.get(String(id));
      if (byId) return byId;
      return lastKnownRef.current.get(DEFAULT_LAST_KNOWN_KEY) ?? null;
    },
    [],
  );

  const mutation = useMutation<unknown, Error, DiffMutationInput>({
    mutationFn: (input) => {
      const { id, original, updated } = input;
      const result = diff(original, updated, strategy);

      if (!result.hasChanges) {
        return Promise.resolve(null);
      }

      const lastKnown = resolveLastKnown(id);
      if (lastKnown) {
        const conflicts = detectConflicts(original, lastKnown, updated);
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
        {
          refreshToken: config.refreshToken,
          onSessionExpired: config.onSessionExpired,
        },
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
      const lastKnown = resolveLastKnown(input.id);
      const conflicts = lastKnown
        ? detectConflicts(input.original, lastKnown, input.updated)
        : [];
      return { diff: result, conflicts };
    },
    [strategy, resolveLastKnown],
  );

  const setLastKnown = useCallback(
    (state: Record<string, unknown>, id?: string | number) => {
      const key = id != null ? String(id) : DEFAULT_LAST_KNOWN_KEY;
      lastKnownRef.current.set(key, state);
    },
    [],
  );

  return {
    ...mutation,
    preview,
    setLastKnown,
  };
}
