import { useCallback, useEffect, useRef, useState } from 'react';
import type { SortOption } from '../../types';
import type {
  AccessibilityState,
  AriaCellProps,
  AriaHeaderCellProps,
  AriaLiveRegionProps,
  AriaRowProps,
  AriaTableProps,
  ColumnConfig,
  FocusPosition,
} from '../use-bifrost-table.types';

export interface UseTableA11yOptions<T> {
  sort: SortOption[];
  columns: ColumnConfig[];
  activeFilterCount: number;
  data: T[];
  visibleColumns: string[];
  editableColumnSet: Set<string>;
  rowKey: string;
  selectedRows: T[];
  expandedRows: Set<string>;
  editingCell: { rowKey: string; field: string } | null;
  tableLabel: string | undefined;
  table: string;
  startEditing: (rowKey: string, field: string) => void;
  cancelEditing: () => void;
  toggleRow: (row: T) => void;
}

/**
 * Owns keyboard-navigation focus and screen-reader announcements, and derives
 * ARIA grid props for the table, rows, cells, and headers.
 */
export function useTableA11y<T = Record<string, unknown>>({
  sort,
  columns,
  activeFilterCount,
  data,
  visibleColumns,
  editableColumnSet,
  rowKey,
  selectedRows,
  expandedRows,
  editingCell,
  tableLabel,
  table,
  startEditing,
  cancelEditing,
  toggleRow,
}: UseTableA11yOptions<T>): AccessibilityState {
  const [focusedCell, setFocusedCell] = useState<FocusPosition | null>(null);
  const [announcement, setAnnouncement] = useState('');
  const prevSortRef = useRef(sort);
  const prevFilterCountRef = useRef(activeFilterCount);
  const prevDataLenRef = useRef(0);

  // Screen reader announcements for data changes
  useEffect(() => {
    const dataLen = data.length;
    if (dataLen !== prevDataLenRef.current) {
      prevDataLenRef.current = dataLen;
      setAnnouncement(
        `Table updated, ${dataLen} row${dataLen === 1 ? '' : 's'} displayed`,
      );
    }
  }, [data.length]);

  // Screen reader announcements for sort changes
  useEffect(() => {
    const prev = prevSortRef.current;
    prevSortRef.current = sort;
    if (prev === sort) return;
    if (sort.length === 0 && prev.length > 0) {
      setAnnouncement('Sort cleared');
    } else if (sort.length > 0) {
      const primary = sort[0];
      const col = columns.find((c) => c.field === primary.field);
      const label = col?.header ?? primary.field;
      setAnnouncement(
        `Sorted by ${label} ${primary.direction === 'asc' ? 'ascending' : 'descending'}`,
      );
    }
  }, [sort, columns]);

  // Screen reader announcements for filter changes
  useEffect(() => {
    const prev = prevFilterCountRef.current;
    prevFilterCountRef.current = activeFilterCount;
    if (prev === activeFilterCount) return;
    if (activeFilterCount === 0 && prev > 0) {
      setAnnouncement('Filters cleared');
    } else if (activeFilterCount > 0) {
      setAnnouncement(
        `${activeFilterCount} filter${activeFilterCount === 1 ? '' : 's'} active`,
      );
    }
  }, [activeFilterCount]);

  const visibleColCount = visibleColumns.length;
  const dataRowCount = data.length;

  const getTableProps = useCallback(
    (label?: string): AriaTableProps => ({
      role: 'grid' as const,
      'aria-label': label ?? tableLabel ?? `${table} data table`,
      'aria-rowcount': dataRowCount + 1,
      'aria-colcount': visibleColCount,
      ...(selectedRows.length > 0 || columns.length > 0
        ? { 'aria-multiselectable': true }
        : {}),
    }),
    [
      tableLabel,
      table,
      dataRowCount,
      visibleColCount,
      selectedRows.length,
      columns.length,
    ],
  );

  const getRowProps = useCallback(
    (rowIndex: number, rk?: string): AriaRowProps => {
      const row = rk
        ? data.find(
            (r) => String((r as Record<string, unknown>)[rowKey]) === rk,
          )
        : undefined;
      const isSelected = row
        ? selectedRows.some(
            (sr) =>
              (sr as Record<string, unknown>)[rowKey] ===
              (row as Record<string, unknown>)[rowKey],
          )
        : undefined;
      const isExpanded = rk ? expandedRows.has(rk) : undefined;
      return {
        role: 'row' as const,
        'aria-rowindex': rowIndex + 1,
        ...(isSelected !== undefined ? { 'aria-selected': isSelected } : {}),
        ...(isExpanded !== undefined ? { 'aria-expanded': isExpanded } : {}),
        tabIndex: focusedCell?.rowIndex === rowIndex ? 0 : -1,
      };
    },
    [data, rowKey, selectedRows, expandedRows, focusedCell],
  );

  const getCellProps = useCallback(
    (colIndex: number, field?: string): AriaCellProps => {
      const isReadOnly = field ? !editableColumnSet.has(field) : true;
      return {
        role: 'gridcell' as const,
        'aria-colindex': colIndex + 1,
        'aria-readonly': isReadOnly,
        tabIndex: focusedCell?.colIndex === colIndex ? 0 : -1,
      };
    },
    [editableColumnSet, focusedCell],
  );

  const getHeaderCellProps = useCallback(
    (colIndex: number, field?: string): AriaHeaderCellProps => {
      let ariaSort: 'ascending' | 'descending' | 'none' = 'none';
      if (field) {
        const s = sort.find((so) => so.field === field);
        if (s) ariaSort = s.direction === 'asc' ? 'ascending' : 'descending';
      }
      return {
        role: 'columnheader' as const,
        'aria-colindex': colIndex + 1,
        'aria-sort': ariaSort,
        tabIndex:
          focusedCell?.rowIndex === -1 && focusedCell?.colIndex === colIndex
            ? 0
            : -1,
      };
    },
    [sort, focusedCell],
  );

  const getLiveRegionProps = useCallback(
    (): AriaLiveRegionProps => ({
      role: 'status' as const,
      'aria-live': 'polite' as const,
      'aria-atomic': true,
    }),
    [],
  );

  const handleKeyDown = useCallback(
    (e: { key: string; preventDefault: () => void; shiftKey?: boolean }) => {
      if (!focusedCell) return;
      const { rowIndex, colIndex } = focusedCell;
      const maxRow = dataRowCount - 1;
      const maxCol = visibleColCount - 1;

      switch (e.key) {
        case 'ArrowUp':
          e.preventDefault();
          if (rowIndex > -1) {
            setFocusedCell({ rowIndex: rowIndex - 1, colIndex });
          }
          break;
        case 'ArrowDown':
          e.preventDefault();
          if (rowIndex < maxRow) {
            setFocusedCell({ rowIndex: rowIndex + 1, colIndex });
          }
          break;
        case 'ArrowLeft':
          e.preventDefault();
          if (colIndex > 0) {
            setFocusedCell({ rowIndex, colIndex: colIndex - 1 });
          }
          break;
        case 'ArrowRight':
          e.preventDefault();
          if (colIndex < maxCol) {
            setFocusedCell({ rowIndex, colIndex: colIndex + 1 });
          }
          break;
        case 'Home':
          e.preventDefault();
          if (e.shiftKey) {
            setFocusedCell({ rowIndex: -1, colIndex: 0 });
          } else {
            setFocusedCell({ rowIndex, colIndex: 0 });
          }
          break;
        case 'End':
          e.preventDefault();
          if (e.shiftKey) {
            setFocusedCell({ rowIndex: maxRow, colIndex: maxCol });
          } else {
            setFocusedCell({ rowIndex, colIndex: maxCol });
          }
          break;
        case 'Enter': {
          e.preventDefault();
          if (rowIndex >= 0 && rowIndex <= maxRow) {
            const visField = visibleColumns[colIndex];
            if (visField && editableColumnSet.has(visField)) {
              const row = data[rowIndex];
              const rk = row
                ? String((row as Record<string, unknown>)[rowKey])
                : undefined;
              if (rk) startEditing(rk, visField);
            }
          }
          break;
        }
        case 'Escape':
          e.preventDefault();
          if (editingCell) {
            cancelEditing();
          } else {
            setFocusedCell(null);
          }
          break;
        case ' ':
          e.preventDefault();
          if (rowIndex >= 0 && rowIndex <= maxRow) {
            const row = data[rowIndex];
            if (row) toggleRow(row);
          }
          break;
      }
    },
    [
      focusedCell,
      dataRowCount,
      visibleColCount,
      visibleColumns,
      editableColumnSet,
      data,
      rowKey,
      startEditing,
      editingCell,
      cancelEditing,
      toggleRow,
    ],
  );

  return {
    getTableProps,
    getRowProps,
    getCellProps,
    getHeaderCellProps,
    getLiveRegionProps,
    announcement,
    keyboard: {
      focusedCell,
      setFocusedCell,
      handleKeyDown,
    },
  };
}
