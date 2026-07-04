import { useCallback, useEffect, useRef, useState } from 'react';
import type { PerformanceState } from '../use-bifrost-table.types';

export interface UseTablePerformanceOptions {
  searchDebounceMs: number;
  isLoading: boolean;
  dataLength: number;
}

/**
 * Owns debounced search input and tracks request metrics (count, last request
 * time, staleness) for the table's data fetching.
 */
export function useTablePerformance({
  searchDebounceMs,
  isLoading,
  dataLength,
}: UseTablePerformanceOptions): PerformanceState {
  const [debouncedSearch, setDebouncedSearch] = useState('');
  const [isSearchPending, setIsSearchPending] = useState(false);
  const searchDebounceTimerRef = useRef<ReturnType<typeof setTimeout>>();
  const requestCountRef = useRef(0);
  const lastRequestTimeRef = useRef<number | null>(null);

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

  // Track request metrics
  useEffect(() => {
    if (isLoading) {
      requestCountRef.current += 1;
      lastRequestTimeRef.current = Date.now();
    }
  }, [isLoading]);

  const isStale = isLoading && dataLength > 0;

  return {
    debouncedSearch,
    setSearch,
    isSearchPending,
    requestCount: requestCountRef.current,
    lastRequestTime: lastRequestTimeRef.current,
    isStale,
  };
}
