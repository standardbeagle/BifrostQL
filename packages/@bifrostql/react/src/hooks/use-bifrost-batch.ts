import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useContext, useCallback, useRef } from 'react';
import { BifrostContext } from '../components/bifrost-provider';
import { executeGraphQL } from '../utils/graphql-client';
import { buildMutation } from '../utils/mutation-builder';
import type { MutationType } from '../utils/mutation-builder';

export interface BatchOperation {
  type: MutationType;
  table: string;
  data?: Record<string, unknown>;
  id?: string | number;
  key?: Record<string, unknown>;
}

export interface BatchProgress {
  total: number;
  completed: number;
  failed: number;
  current: number;
}

export interface BatchResult {
  results: Array<{ index: number; data: unknown }>;
  errors: Array<{ index: number; error: Error }>;
}

export interface UseBifrostBatchOptions {
  allowPartialSuccess?: boolean;
  invalidateQueries?: string[];
  onProgress?: (progress: BatchProgress) => void;
  onSuccess?: (result: BatchResult) => void;
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

export class BatchError extends Error {
  readonly results: BatchResult['results'];
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
