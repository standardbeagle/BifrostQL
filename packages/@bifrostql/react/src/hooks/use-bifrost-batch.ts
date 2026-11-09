import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useContext, useCallback, useRef } from 'react';
import { BifrostContext } from '../components/bifrost-provider';
import { BifrostGraphQLError, executeGraphQL } from '../utils/graphql-client';
import { buildBatchMutation } from '../utils/mutation-builder';
import type { MutationType } from '../utils/mutation-builder';
import { invalidateBifrostQueries } from '../utils/invalidation';

/** A single operation in a batch. */
export interface BatchOperation {
  /** The mutation type to execute. */
  type: MutationType;
  /** The database table name. */
  table: string;
  /** Row data for insert, update, or upsert operations. */
  data?: Record<string, unknown>;
  /** Row ID for update or delete operations. */
  id?: string | number;
  /** Primary key for upsert operations, or a composite or renamed one for update and delete. */
  key?: Record<string, unknown>;
}

/**
 * Progress of a running batch. A document commits or rolls back whole, so
 * `completed` and `failed` move by a document's operation count at a time; a
 * single-table batch goes from none to all in one step.
 */
export interface BatchProgress {
  /** Total number of operations in the batch. */
  total: number;
  /** Operations in documents that committed. */
  completed: number;
  /** Operations in the document that failed. */
  failed: number;
  /** Number of documents the batch sends. */
  documents: number;
  /** Documents answered so far, which is also the zero-based index of the one in flight. */
  documentsSettled: number;
}

/** One `<table>_batch` document that committed. */
export interface BatchDocumentResult {
  /** The table the document wrote to. */
  table: string;
  /** Original indices of the document's operations, in the order they were applied. */
  indices: number[];
  /**
   * Rows the server reports affected across the whole document. It is not an
   * operation count: an update or delete that matches no row adds 0 and does
   * not fail. The server returns no per-row result, so inserted keys are not
   * available here.
   */
  affected: number;
}

/** The result of a batch whose every document committed. */
export interface BatchResult {
  /** One entry per document, in the order they were sent. */
  results: BatchDocumentResult[];
}

/** The document that stopped a batch. */
export interface BatchFailure {
  /** The table the document targeted. */
  table: string;
  /** Original indices of all of the document's operations. */
  indices: number[];
  /**
   * What the request failed with: a `BifrostGraphQLError` when the server refused
   * the document. The server reports no failing row index.
   */
  error: Error;
}

/** Options for the {@link useBifrostBatch} hook. */
export interface UseBifrostBatchOptions {
  /**
   * Queries to invalidate once anything has committed — table names or full
   * query strings; see {@link invalidateBifrostQueries} for the matching rules.
   */
  invalidateQueries?: string[];
  /** Callback invoked when the batch starts and as each document settles. */
  onProgress?: (progress: BatchProgress) => void;
  /** Callback invoked when every document has committed. */
  onSuccess?: (result: BatchResult) => void;
  /** Callback invoked when the batch fails, with a {@link BatchError} if anything was sent. */
  onError?: (error: Error) => void;
}

interface IndexedOperation {
  index: number;
  operation: BatchOperation;
}

/** The operations one `<table>_batch` request carries. */
interface BatchDocument {
  table: string;
  query: string;
  actions: Array<Record<string, Record<string, unknown>>>;
  indices: number[];
}

/**
 * Resolves the row identity used by `update` / `delete` actions.
 *
 * Prefers the composite `key` when supplied. Otherwise uses `{ id }`, which
 * assumes a single primary key literally named `id` — tables with a composite
 * or renamed primary key MUST pass `op.key` instead of `op.id`.
 */
function identityFor(
  op: BatchOperation,
  index: number,
): Record<string, unknown> {
  if (op.key && Object.keys(op.key).length > 0) {
    return { ...op.key };
  }
  if (op.id != null) {
    return { id: op.id };
  }
  // An empty identity would send an unqualified update/delete: either a no-op
  // the caller reads as success, or a write that hits the wrong rows. Neither
  // is recoverable once it has left, so refuse to build the operation.
  throw new Error(
    `Batch operation ${index} (${op.type} on "${op.table}") has no row identity: supply \`id\` for a single-\`id\` table, or \`key\` for a composite or renamed primary key.`,
  );
}

function actionDetail(
  op: BatchOperation,
  index: number,
): Record<string, unknown> {
  switch (op.type) {
    case 'insert':
      return { ...op.data };
    case 'update':
      return { ...op.data, ...identityFor(op, index) };
    case 'upsert':
      return { ...op.key, ...op.data };
    case 'delete':
      return identityFor(op, index);
  }
}

function buildAction(
  op: BatchOperation,
  index: number,
): BatchDocument['actions'][number] {
  const detail = actionDetail(op, index);
  // The server skips an action with no columns without an error, so it would
  // read as a success that wrote nothing.
  if (Object.keys(detail).length === 0) {
    throw new Error(
      `Batch operation ${index} (${op.type} on "${op.table}") has no columns.`,
    );
  }
  return { [op.type]: detail };
}

const TYPE_ORDER: Record<MutationType, number> = {
  insert: 0,
  upsert: 1,
  update: 2,
  delete: 3,
};

/**
 * True when the caller placed a `delete` before an `insert`/`upsert` on the
 * same table — the "replace" pattern (drop a row, then re-create it under the
 * same unique key). Reordering that into insert-then-delete would clobber the
 * freshly inserted row, so the caller's ordering must be preserved verbatim.
 */
function hasReplaceIntent(indexed: IndexedOperation[]): boolean {
  const tablesWithDelete = new Set<string>();
  for (const { operation } of indexed) {
    if (operation.type === 'delete') {
      tablesWithDelete.add(operation.table);
    } else if (
      (operation.type === 'insert' || operation.type === 'upsert') &&
      tablesWithDelete.has(operation.table)
    ) {
      return true;
    }
  }
  return false;
}

/**
 * Orders operations by dependency phase (insert → upsert → update → delete) for
 * referential integrity, and within a phase gathers each table's operations at
 * its first mention in that phase, so the caller's parent-before-child order
 * holds with as few documents as it can. A same-table delete-then-insert/upsert
 * "replace" skips the sort, so the caller's order is honored and the replace
 * isn't inverted.
 */
function sortByDependencyOrder(
  indexed: IndexedOperation[],
): IndexedOperation[] {
  if (hasReplaceIntent(indexed)) {
    return indexed;
  }
  const firstMention = new Map<string, number>();
  const phaseTable = ({ type, table }: BatchOperation) => `${type}:${table}`;
  indexed.forEach(({ operation }, position) => {
    const key = phaseTable(operation);
    if (!firstMention.has(key)) firstMention.set(key, position);
  });
  return [...indexed].sort(
    (a, b) =>
      TYPE_ORDER[a.operation.type] - TYPE_ORDER[b.operation.type] ||
      firstMention.get(phaseTable(a.operation))! -
        firstMention.get(phaseTable(b.operation))!,
  );
}

/**
 * Turns operations into the `<table>_batch` documents to send, one for each run
 * of a table in dependency order. Everything is built before anything is sent,
 * so an operation that cannot be built refuses the batch with nothing written.
 */
function planBatchDocuments(operations: BatchOperation[]): BatchDocument[] {
  const documents: BatchDocument[] = [];
  const indexed = operations.map((operation, index) => ({ index, operation }));
  for (const { index, operation } of sortByDependencyOrder(indexed)) {
    let run = documents[documents.length - 1];
    if (run?.table !== operation.table) {
      run = {
        table: operation.table,
        query: buildBatchMutation(operation.table),
        actions: [],
        indices: [],
      };
      documents.push(run);
    }
    run.actions.push(buildAction(operation, index));
    run.indices.push(index);
  }
  return documents;
}

/**
 * Hook that writes many operations through the server's `<table>_batch` field.
 *
 * **Transactions.** Each document is one transaction: all of its operations
 * commit or none do, on the server's set-based fast path when the table is
 * eligible. All operations on one table go in one document, unless
 * operations on another table must run between them. **BifrostQL has no
 * transaction across tables for this**, so a batch over several tables is
 * several transactions: a later document's failure does not undo an earlier
 * one, and {@link BatchError} says which committed. For one transaction over
 * rows linked by foreign keys, write a `save` graph mutation instead.
 *
 * **Order.** Operations run insert → upsert → update → delete, and within a verb
 * tables run in the order the caller first mentions them, so list parents before
 * children for inserts and children before parents for deletes. A same-table
 * delete-then-insert/upsert "replace" keeps the caller's order for the whole
 * batch instead.
 *
 * **Failure.** The batch stops at the first failed document and rejects with a
 * {@link BatchError}. There is no partial success within a document, and the
 * server reports no failing row index. A lost connection cannot tell whether
 * the document committed.
 *
 * **Server limits apply to each document.** A document over the table's
 * `batch-max-size` (default 100) is refused whole; the hook does not split it,
 * because that would quietly make it several transactions. Under the table's
 * `batch-duplicate-policy`, several updates or deletes of one key collapse to
 * their net effect or are refused, and an upsert sharing a key with an update,
 * delete or another upsert is always refused, which includes a delete-then-upsert
 * replace.
 *
 * Must be used within a {@link BifrostProvider}.
 *
 * @param options - Invalidation and callbacks.
 * @returns A TanStack Query mutation result with an additional `getProgress` method.
 *
 * @example
 * ```tsx
 * const { mutate, getProgress } = useBifrostBatch({
 *   invalidateQueries: ['users'],
 *   onSuccess: ({ results }) => console.log(results[0].affected),
 * });
 *
 * // One document, one transaction: all three apply or none do.
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
    documents: 0,
    documentsSettled: 0,
  });

  const getProgress = useCallback(() => ({ ...progressRef.current }), []);

  const reportProgress = (progress: BatchProgress) => {
    progressRef.current = progress;
    options.onProgress?.({ ...progress });
  };

  const mutation = useMutation<BatchResult, Error, BatchOperation[]>({
    mutationFn: async (operations) => {
      const documents = planBatchDocuments(operations);
      const results: BatchDocumentResult[] = [];

      reportProgress({
        total: operations.length,
        completed: 0,
        failed: 0,
        documents: documents.length,
        documentsSettled: 0,
      });

      for (const [position, batchDocument] of documents.entries()) {
        const { table, query, actions, indices } = batchDocument;
        const settled = position + 1;
        try {
          const data = await executeGraphQL<Record<string, unknown>>(
            config.endpoint,
            config.headers ?? {},
            query,
            { actions },
            undefined,
            config.getToken,
            {
              refreshToken: config.refreshToken,
              onSessionExpired: config.onSessionExpired,
            },
          );
          const affected = data[`${table}_batch`];
          if (typeof affected !== 'number') {
            throw new Error(
              `BifrostQL reply for ${table}_batch has no affected-row count.`,
            );
          }
          results.push({ table, indices, affected });
          reportProgress({
            ...progressRef.current,
            completed: progressRef.current.completed + indices.length,
            documentsSettled: settled,
          });
        } catch (err) {
          const error = err instanceof Error ? err : new Error(String(err));
          reportProgress({
            ...progressRef.current,
            failed: indices.length,
            documentsSettled: settled,
          });
          throw new BatchError(
            `Batch on "${table}" failed: ${error.message}`,
            results,
            { table, indices, error },
            documents.slice(settled).flatMap((later) => later.indices),
          );
        }
      }

      return { results };
    },
    onSuccess: options.onSuccess,
    onError: options.onError,
    onSettled: (result, error) => {
      if (!options.invalidateQueries) return;
      // A failed batch can still have committed earlier documents, and the UI
      // would show pre-write data for those rows indefinitely. A document that
      // rolled back wrote nothing, so a batch with no commit leaves the cache
      // alone.
      const committed =
        result !== undefined ||
        (error instanceof BatchError && error.results.length > 0);
      if (!committed) return;
      invalidateBifrostQueries(queryClient, options.invalidateQueries);
    },
  });

  return {
    ...mutation,
    getProgress,
  };
}

/**
 * Error thrown when a batch document fails.
 *
 * `failed`'s document did not commit, unless the connection was lost and its
 * outcome is unknown. A table gated by `approval` also refuses this way, having
 * queued its operations as pending changes rather than applying them.
 */
export class BatchError extends Error {
  /** Documents that committed before the failure. These are not undone. */
  readonly results: BatchDocumentResult[];
  /** The document that failed. */
  readonly failed: BatchFailure;
  /** Original indices of the operations in later documents, which were never sent. */
  readonly notAttempted: number[];
  /** The server's error code for the failed document, when it sent one. */
  readonly code?: string;

  constructor(
    message: string,
    results: BatchDocumentResult[],
    failed: BatchFailure,
    notAttempted: number[],
  ) {
    super(message);
    this.name = 'BatchError';
    this.results = results;
    this.failed = failed;
    this.notAttempted = notAttempted;
    this.code =
      failed.error instanceof BifrostGraphQLError
        ? failed.error.code
        : undefined;
  }
}
