import { useCallback, useState } from 'react';
import type { SelectionState } from '../use-bifrost-table.types';

export interface UseTableSelectionResult<T> {
  selection: SelectionState<T>;
  toggleRow: (row: T) => void;
}

/** Owns row-selection state keyed by `rowKey`. */
export function useTableSelection<T = Record<string, unknown>>(
  rowKey: string,
): UseTableSelectionResult<T> {
  const [selectedRows, setSelectedRows] = useState<T[]>([]);

  const toggleRow = useCallback(
    (row: T) => {
      setSelectedRows((prev) => {
        const key = (row as Record<string, unknown>)[rowKey];
        const exists = prev.some(
          (r) => (r as Record<string, unknown>)[rowKey] === key,
        );
        if (exists) {
          return prev.filter(
            (r) => (r as Record<string, unknown>)[rowKey] !== key,
          );
        }
        return [...prev, row];
      });
    },
    [rowKey],
  );

  const selectAll = useCallback((rows: T[]) => {
    setSelectedRows(rows);
  }, []);

  const clearSelection = useCallback(() => {
    setSelectedRows([]);
  }, []);

  return {
    selection: {
      selectedRows,
      toggleRow,
      selectAll,
      clearSelection,
    },
    toggleRow,
  };
}
