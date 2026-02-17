import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useContext, useCallback, useRef } from 'react';
import { BifrostContext } from '../components/bifrost-provider';
import { executeGraphQL } from '../utils/graphql-client';
import { buildMutation } from '../utils/mutation-builder';
import type { MutationType } from '../utils/mutation-builder';

/** A single operation in a batch mutation sequence. */
export interface BatchOperation {
  /** The mutation type to execute. */
  type: MutationType;
  /** The database table name. */
  table: string;
  /** Row data for insert, update, or upsert operations. */
  data?: Record<string, unknown>;
  /** Row ID for update or delete operations. */
  id?: string | number;
  /** Composite key for upsert operations. */
  key?: Record<string, unknown>;
}

/** Progress information for a running batch operation. */
export interface BatchProgress {
  /** Total number of operations in the batch. */
  total: number;
  /** Number of operations completed successfully. */
  completed: number;
  /** Number of operations that failed. */
  failed: number;
  /** Zero-based index of the currently executing operation. */
  current: number;
}

/** The result of a completed batch execution. */
export interface BatchResult {
  /** Successfully completed operations with their original indices. */
  results: Array<{ index: number; data: unknown }>;
  /** Failed operations with their original indices and errors. */
  errors: Array<{ index: number; error: Error }>;
}

/** Options for the {@link useBifrostBatch} hook. */
export interface UseBifrostBatchOptions {
  /**
   * When `true`, the batch continues after individual failures.
   * When `false` (default), a single failure aborts the entire batch
   * and throws a {@link BatchError}.
   */
  allowPartialSuccess?: boolean;
  /** Query keys to invalidate on success. */
  invalidateQueries?: string[];
  /** Callback invoked after each operation completes or fails. */
  onProgress?: (progress: BatchProgress) => void;
  /** Callback invoked when the entire batch completes. */
  onSuccess?: (result: BatchResult) => void;
  /** Callback invoked when the batch fails (only in strict mode). */
  onError?: (error: Error) => void;
}

interface IndexedOperation {
  index: number;
  operation: BatchOperation;
}

function buildVariables(op: BatchOperation): Record<string, unknown> {
  switch (op.type) {
    case 'insert':
      return { detail: op.data };
    case 'update':
      return {
        detail: { ...op.data, ...(op.id != null ? { id: op.id } : {}) },
      };
    case 'upsert':
      return { detail: { ...op.key, ...op.data } };
    case 'delete':
      return { detail: { id: op.id } };
  }
}

const TYPE_ORDER: Record<MutationType, number> = {
  insert: 0,
  upsert: 1,
  update: 2,
  delete: 3,
};

function sortByDependencyOrder(
  indexed: IndexedOperation[],
): IndexedOperation[] {
  return [...indexed].sort(
    (a, b) => TYPE_ORDER[a.operation.type] - TYPE_ORDER[b.operation.type],
  );
}

/**
 * Hook for executing multiple mutations sequentially with progress tracking.
 *
 * Operations are automatically sorted by dependency order: inserts first,
 * then upserts, updates, and finally deletes. This ensures referential
 * integrity when operations depend on each other.
 *
 * Must be used within a {@link BifrostProvider}.
 *
 * @param options - Batch configuration including partial success mode and callbacks.
 * @returns A TanStack Query mutation result with an additional `getProgress` method.
 *
 * @example
 * ```tsx
 * const { mutate, getProgress } = useBifrostBatch({
 *   allowPartialSuccess: true,
 *   invalidateQueries: ['users'],
 *   onProgress: ({ completed, total }) => console.log(`${completed}/${total}`),
 * });
 *
 * mutate([
 *   { type: 'insert', table: 'users', data: { name: 'Bob' } },
 *   { type: 'update', table: 'users', id: 1, data: { name: 'Alice Updated' } },
 *   { type: 'delete', table: 'users', id: 99 },
 * ]);
 * ```
 */
export function useBifrostBatch(options: UseBifrostBatchOptions = {}) {
  const config = useContext(BifrostContext);
  if (!config) {
    throw new Error('useBifrostBatch must be used within a BifrostProvider');
  }

  const queryClient = useQueryClient();
  const progressRef = useRef<BatchProgress>({
    total: 0,
    completed: 0,
    failed: 0,
    current: 0,
  });

  const getProgress = useCallback(() => ({ ...progressRef.current }), []);

  const mutation = useMutation<BatchResult, Error, BatchOperation[]>({
    mutationFn: async (operations) => {
      const indexed = operations.map((operation, index) => ({
        index,
        operation,
      }));
      const sorted = sortByDependencyOrder(indexed);
      const results: BatchResult['results'] = [];
      const errors: BatchResult['errors'] = [];

      progressRef.current = {
        total: sorted.length,
        completed: 0,
        failed: 0,
        current: 0,
      };
      options.onProgress?.({ ...progressRef.current });

      for (let i = 0; i < sorted.length; i++) {
        const { index: originalIndex, operation: op } = sorted[i];
        progressRef.current = { ...progressRef.current, current: i };
        options.onProgress?.({ ...progressRef.current });

        try {
          const gql = buildMutation(op.table, op.type);
          const variables = buildVariables(op);
          const data = await executeGraphQL(
            config.endpoint,
            config.headers ?? {},
            gql,
            variables,
            undefined,
            config.getToken,
          );
          results.push({ index: originalIndex, data });
          progressRef.current = {
            ...progressRef.current,
            completed: progressRef.current.completed + 1,
          };
          options.onProgress?.({ ...progressRef.current });
        } catch (err) {
          const error = err instanceof Error ? err : new Error(String(err));
          errors.push({ index: originalIndex, error });
          progressRef.current = {
            ...progressRef.current,
            failed: progressRef.current.failed + 1,
          };
          options.onProgress?.({ ...progressRef.current });

          if (!options.allowPartialSuccess) {
            throw new BatchError(
              `Batch operation failed at index ${originalIndex}: ${error.message}`,
              results,
              errors,
            );
          }
        }
      }

      return { results, errors };
    },
    onSuccess: (result) => {
      if (options.invalidateQueries) {
        for (const key of options.invalidateQueries) {
          queryClient.invalidateQueries({ queryKey: ['bifrost', key] });
        }
      }
      options.onSuccess?.(result);
    },
    onError: options.onError,
  });

  return {
    ...mutation,
    getProgress,
  };
}

/**
 * Error thrown when a batch operation fails in strict mode (`allowPartialSuccess: false`).
 *
 * Contains both the successful results and the errors accumulated before the failure,
 * allowing the caller to inspect partial progress.
 */
export class BatchError extends Error {
  /** Operations that completed successfully before the failure. */
  readonly results: BatchResult['results'];
  /** Operations that failed, including the one that caused the abort. */
  readonly errors: BatchResult['errors'];

  constructor(
    message: string,
    results: BatchResult['results'],
    errors: BatchResult['errors'],
  ) {
    super(message);
    this.name = 'BatchError';
    this.results = results;
    this.errors = errors;
  }
}
