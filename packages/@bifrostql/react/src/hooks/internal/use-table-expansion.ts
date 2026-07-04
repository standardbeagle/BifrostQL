import { useCallback, useContext, useEffect, useRef, useState } from 'react';
import { BifrostContext } from '../../components/bifrost-provider';
import { executeGraphQL } from '../../utils/graphql-client';
import { buildGraphqlQuery } from '../../utils/query-builder';
import type {
  ChildQueryConfig,
  ChildRowData,
  ExpansionState,
} from '../use-bifrost-table.types';

export interface UseTableExpansionOptions {
  expandable: boolean;
  childQuery: ChildQueryConfig | undefined;
  rowKey: string;
}

export interface UseTableExpansionResult {
  expansion: ExpansionState;
  expandedRows: Set<string>;
}

/**
 * Owns row-expansion state plus lazy fetching, caching, and abort handling of
 * child/detail rows for expanded parents.
 */
export function useTableExpansion({
  expandable,
  childQuery,
  rowKey,
}: UseTableExpansionOptions): UseTableExpansionResult {
  const bifrostConfig = useContext(BifrostContext);
  const [expandedRows, setExpandedRows] = useState<Set<string>>(new Set());
  const childCacheRef = useRef<Map<string, unknown[]>>(new Map());
  const [childLoadingRows, setChildLoadingRows] = useState<Set<string>>(
    new Set(),
  );
  const [childErrors, setChildErrors] = useState<Map<string, Error>>(new Map());
  const childAbortRef = useRef<Map<string, AbortController>>(new Map());

  const toggleExpand = useCallback(
    (rowId: string) => {
      if (!expandable) return;
      setExpandedRows((prev) => {
        const next = new Set(prev);
        if (next.has(rowId)) {
          next.delete(rowId);
        } else {
          next.add(rowId);
        }
        return next;
      });
    },
    [expandable],
  );

  const expandAll = useCallback(
    (rowIds: string[]) => {
      if (!expandable) return;
      setExpandedRows(new Set(rowIds));
    },
    [expandable],
  );

  const collapseAll = useCallback(() => {
    setExpandedRows(new Set());
  }, []);

  const fetchChildData = useCallback(
    (rowId: string, parentRow: Record<string, unknown>) => {
      if (!childQuery || !bifrostConfig) return;
      if (childCacheRef.current.has(rowId)) return;
      if (childLoadingRows.has(rowId)) return;

      const existing = childAbortRef.current.get(rowId);
      if (existing) existing.abort();
      const controller = new AbortController();
      childAbortRef.current.set(rowId, controller);

      setChildLoadingRows((prev) => {
        const next = new Set(prev);
        next.add(rowId);
        return next;
      });
      setChildErrors((prev) => {
        if (!prev.has(rowId)) return prev;
        const next = new Map(prev);
        next.delete(rowId);
        return next;
      });

      const parentKeyField = childQuery.parentKeyField ?? rowKey;
      const childFilterField = childQuery.childFilterField ?? parentKeyField;
      const parentValue = parentRow[parentKeyField];

      const query = buildGraphqlQuery(childQuery.query, {
        fields: childQuery.fields,
        filter: { [childFilterField]: { _eq: parentValue as string | number } },
      });

      executeGraphQL<Record<string, unknown[]>>(
        bifrostConfig.endpoint,
        bifrostConfig.headers ?? {},
        query,
        undefined,
        controller.signal,
        bifrostConfig.getToken,
        {
          refreshToken: bifrostConfig.refreshToken,
          onSessionExpired: bifrostConfig.onSessionExpired,
        },
      )
        .then((data) => {
          const childData = data[childQuery.query] ?? [];
          childCacheRef.current.set(rowId, childData);
          setChildLoadingRows((prev) => {
            const next = new Set(prev);
            next.delete(rowId);
            return next;
          });
          childAbortRef.current.delete(rowId);
        })
        .catch((err: unknown) => {
          if (err instanceof Error && err.name === 'AbortError') return;
          childAbortRef.current.delete(rowId);
          setChildLoadingRows((prev) => {
            const next = new Set(prev);
            next.delete(rowId);
            return next;
          });
          setChildErrors((prev) => {
            const next = new Map(prev);
            next.set(
              rowId,
              err instanceof Error ? err : new Error(String(err)),
            );
            return next;
          });
        });
    },
    [childQuery, bifrostConfig, childLoadingRows, rowKey],
  );

  const getChildData = useCallback(
    (rowId: string): ChildRowData => ({
      data: (childCacheRef.current.get(rowId) as unknown[] | null) ?? null,
      loading: childLoadingRows.has(rowId),
      error: childErrors.get(rowId) ?? null,
    }),
    [childLoadingRows, childErrors],
  );

  const isChildLoading = useCallback(
    (rowId: string): boolean => childLoadingRows.has(rowId),
    [childLoadingRows],
  );

  const childErrorFn = useCallback(
    (rowId: string): Error | null => childErrors.get(rowId) ?? null,
    [childErrors],
  );

  const clearChildCache = useCallback((rowId?: string) => {
    if (rowId) {
      childCacheRef.current.delete(rowId);
      const controller = childAbortRef.current.get(rowId);
      if (controller) {
        controller.abort();
        childAbortRef.current.delete(rowId);
      }
    } else {
      childCacheRef.current.clear();
      for (const controller of childAbortRef.current.values()) {
        controller.abort();
      }
      childAbortRef.current.clear();
    }
    setChildLoadingRows(new Set());
    setChildErrors(new Map());
  }, []);

  // Abort in-flight child requests on unmount
  useEffect(() => {
    const abortMap = childAbortRef.current;
    return () => {
      for (const controller of abortMap.values()) {
        controller.abort();
      }
    };
  }, []);

  return {
    expansion: {
      expandedRows,
      toggleExpand,
      expandAll,
      collapseAll,
      getChildData,
      fetchChildData,
      isChildLoading,
      childError: childErrorFn,
      clearChildCache,
    },
    expandedRows,
  };
}
