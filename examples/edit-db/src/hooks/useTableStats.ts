import { useQuery } from "@tanstack/react-query";
import { useFetcher } from "../common/fetcher";
import { useSchema } from "./useSchema";
import { useMemo } from "react";

/**
 * Interface representing statistics for a single table.
 * @interface TableStats
 */
export interface TableStats {
  /** Number of columns in the table */
  columnCount: number;
  /** Number of rows in the table (null if loading/error) */
  rowCount: number | null;
  /** Number of foreign key relationships (singleJoins) */
  fkCount: number;
  /** Whether row count is currently loading */
  isLoading: boolean;
  /** Error message if row count fetch failed */
  error: string | null;
}

/**
 * Type for table stats record keyed by table name.
 * @type TableStatsMap
 */
export type TableStatsMap = Record<string, TableStats>;

/**
 * Result type for the useTableStats hook.
 * @interface UseTableStatsResult
 */
export interface UseTableStatsResult {
  /** Map of table names to their stats */
  stats: TableStatsMap;
  /** Whether any stats are loading */
  isLoading: boolean;
  /** Error if schema loading failed */
  error: { message: string } | null;
}

/**
 * Builds a GraphQL query to fetch row counts for all tables.
 * 
 * @param tableNames - Array of table GraphQL names
 * @returns GraphQL query string
 */
function buildRowCountQuery(tableNames: string[]): string | null {
  if (tableNames.length === 0) return null;

  const queries = tableNames.map((name) => {
    return `${name}: ${name}(limit: 1) { total }`;
  });

  return `query GetTableRowCounts { ${queries.join(" ")} }`;
}

/**
 * Hook to fetch table statistics including row counts.
 * 
 * Automatically fetches row counts for all tables in the schema.
 * Column counts and FK counts are derived from schema metadata (synchronous).
 * Row counts are fetched via GraphQL (asynchronous).
 * 
 * @example
 * ```tsx
 * const { stats, isLoading } = useTableStats();
 * const customerStats = stats['customers'];
 * // customerStats = { columnCount: 12, rowCount: 1250, fkCount: 3, isLoading: false, error: null }
 * ```
 * 
 * @returns Table statistics map and loading state
 */
export function useTableStats(): UseTableStatsResult {
  const { data: tables, loading: schemaLoading, error: schemaError } = useSchema();
  const fetcher = useFetcher();

  const tableNames = useMemo(() => tables.map((t) => t.name), [tables]);

  const rowCountQuery = useMemo(
    () => buildRowCountQuery(tableNames),
    [tableNames]
  );

  const {
    data: rowCountData,
    isLoading: rowCountLoading,
    error: rowCountError,
  } = useQuery({
    queryKey: ["tableRowCounts", tableNames],
    queryFn: () => fetcher.query<Record<string, { total: number }>>(rowCountQuery!),
    enabled: !!rowCountQuery && tableNames.length > 0,
    staleTime: 5 * 60 * 1000, // 5 minutes
  });

  const stats = useMemo((): TableStatsMap => {
    const result: TableStatsMap = {};

    for (const table of tables) {
      const rowCountResult = rowCountData?.[table.name];
      const rowCount = rowCountResult?.total ?? null;

      result[table.name] = {
        columnCount: table.columns.length,
        rowCount,
        fkCount: table.singleJoins.length,
        isLoading: rowCountLoading,
        error: rowCountError ? (rowCountError as Error).message : null,
      };
    }

    return result;
  }, [tables, rowCountData, rowCountLoading, rowCountError]);

  return {
    stats,
    isLoading: schemaLoading || rowCountLoading,
    error: schemaError,
  };
}

/**
 * Formats a number into an abbreviated string representation.
 * 
 * @example
 * ```typescript
 * abbreviateNumber(12)      // "12"
 * abbreviateNumber(1200)    // "1.2k"
 * abbreviateNumber(45000)   // "45k"
 * abbreviateNumber(1200000) // "1.2M"
 * ```
 * 
 * @param num - Number to abbreviate
 * @returns Abbreviated string representation
 */
export function abbreviateNumber(num: number | null): string {
  if (num === null) return "—";
  if (num === 0) return "0";
  if (num < 1000) return num.toString();
  if (num < 1000000) {
    const k = num / 1000;
    return k % 1 === 0 ? `${k.toFixed(0)}k` : `${k.toFixed(1)}k`;
  }
  const m = num / 1000000;
  return m % 1 === 0 ? `${m.toFixed(0)}M` : `${m.toFixed(1)}M`;
}

/**
 * Calculates a visual bar width (1-10) based on row count.
 * Used for sparkline visualization.
 * 
 * @param count - Row count
 * @param maxCount - Maximum row count across all tables for scaling
 * @returns Width value from 1-10
 */
export function calculateBarWidth(count: number | null, maxCount: number): number {
  if (count === null || maxCount === 0) return 0;
  const ratio = count / maxCount;
  return Math.max(1, Math.min(10, Math.ceil(ratio * 10)));
}
