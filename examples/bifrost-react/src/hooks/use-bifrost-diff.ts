import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useContext, useRef, useCallback } from 'react';
import { BifrostContext } from '../components/bifrost-provider';
import { executeGraphQL } from '../utils/graphql-client';
import { buildUpdateMutation } from '../utils/mutation-builder';
import { diff, detectConflicts } from '../utils/diff-engine';
import type { DiffStrategy, DiffResult } from '../utils/diff-engine';

export interface UseBifrostDiffOptions {
  table: string;
  idField: string;
  strategy?: DiffStrategy;
  invalidateQueries?: string[];
  onSuccess?: (data: unknown) => void;
  onError?: (error: Error) => void;
}

export interface DiffMutationInput {
  id: string | number;
  original: Record<string, unknown>;
  updated: Record<string, unknown>;
}

export interface DiffMutationResult {
  diff: DiffResult;
  conflicts: string[];
}

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
        const conflicts = detectConflicts(original, lastKnownRef.current, updated);
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

  const setLastKnown = useCallback(
    (state: Record<string, unknown>) => {
      lastKnownRef.current = state;
    },
    [],
  );

  return {
    ...mutation,
    preview,
    setLastKnown,
  };
}
