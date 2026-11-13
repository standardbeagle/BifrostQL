import { useCallback, useEffect, useRef, useState } from 'react';
import type { SearchState } from '../use-bifrost-table.types';

export interface UseTableSearchOptions {
  searchDebounceMs: number;
  isLoading: boolean;
  dataLength: number;
}

/**
 * Owns debounced search input and tracks request metrics (count, last request
 * time, staleness) for the table's data fetching.
 */
export function useTableSearch({
  searchDebounceMs,
  isLoading,
  dataLength,
}: UseTableSearchOptions): SearchState {
  const [debouncedSearch, setDebouncedSearch] = useState('');
  const [isSearchPending, setIsSearchPending] = useState(false);
  const searchDebounceTimerRef = useRef<ReturnType<typeof setTimeout>>();
  // Request metrics, derived while rendering from the loading flag's previous
  // value: each false -> true transition is one request.
  const [requestMetrics, setRequestMetrics] = useState<{
    wasLoading: boolean;
    count: number;
    lastTime: number | null;
  }>({ wasLoading: false, count: 0, lastTime: null });
  if (isLoading !== requestMetrics.wasLoading) {
    setRequestMetrics((prev) =>
      isLoading
        ? { wasLoading: true, count: prev.count + 1, lastTime: Date.now() }
        : { ...prev, wasLoading: false },
    );
  }

  const setSearch = useCallback(
    (value: string) => {
      setIsSearchPending(true);

      if (searchDebounceTimerRef.current) {
        clearTimeout(searchDebounceTimerRef.current);
      }

      searchDebounceTimerRef.current = setTimeout(() => {
        setDebouncedSearch(value);
        setIsSearchPending(false);
      }, searchDebounceMs);
    },
    [searchDebounceMs],
  );

  useEffect(() => {
    return () => {
      if (searchDebounceTimerRef.current) {
        clearTimeout(searchDebounceTimerRef.current);
      }
    };
  }, []);

  const isStale = isLoading && dataLength > 0;

  return {
    debouncedSearch,
    setSearch,
    isSearchPending,
    requestCount: requestMetrics.count,
    lastRequestTime: requestMetrics.lastTime,
    isStale,
  };
}
