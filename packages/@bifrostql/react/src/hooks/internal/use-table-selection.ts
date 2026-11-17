import { useCallback, useState } from 'react';
import type { SelectionState } from '../use-bifrost-table.types';
import { sameRowKey, type RowKeyColumns } from '../../utils/row-identity';

export interface UseTableSelectionResult<T> {
  selection: SelectionState<T>;
  toggleRow: (row: T) => void;
}

/** Owns row-selection state keyed by `rowKey` (every key column). */
export function useTableSelection<T = Record<string, unknown>>(
  rowKey: RowKeyColumns,
): UseTableSelectionResult<T> {
  const [selectedRows, setSelectedRows] = useState<T[]>([]);

  const toggleRow = useCallback(
    (row: T) => {
      setSelectedRows((prev) => {
        const target = row as Record<string, unknown>;
        const isTarget = (r: T) =>
          sameRowKey(r as Record<string, unknown>, target, rowKey);
        if (prev.some(isTarget)) {
          return prev.filter((r) => !isTarget(r));
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
