import { useCallback, useMemo, useRef, useState } from 'react';
import type {
  ColumnConfig,
  EditingState,
  RowEditState,
  RowUpdateFn,
  BatchSaveFn,
} from '../use-bifrost-table.types';

export interface UseTableEditingOptions<T> {
  columns: ColumnConfig[];
  editable: boolean;
  data: T[];
  rowKey: string;
  autoSave: boolean;
  onRowUpdate: RowUpdateFn | undefined;
  onBatchSave: BatchSaveFn | undefined;
  refetch: () => void;
}

export interface UseTableEditingResult {
  editing: EditingState;
  editableColumnSet: Set<string>;
  editingCell: { rowKey: string; field: string } | null;
  startEditing: (rowKey: string, field: string) => void;
  cancelEditing: () => void;
}

/**
 * Owns inline-editing state: the active cell, per-row dirty changes and
 * validation errors, commit/save/discard flows, and optimistic row updates.
 */
export function useTableEditing<T = Record<string, unknown>>({
  columns,
  editable,
  data,
  rowKey,
  autoSave,
  onRowUpdate,
  onBatchSave,
  refetch,
}: UseTableEditingOptions<T>): UseTableEditingResult {
  const [editingCell, setEditingCell] = useState<{
    rowKey: string;
    field: string;
  } | null>(null);
  const [dirtyRows, setDirtyRows] = useState<Map<string, RowEditState>>(
    () => new Map(),
  );
  const optimisticRollbackRef = useRef<Map<string, Record<string, unknown>>>(
    new Map(),
  );

  const editableColumnSet = useMemo(() => {
    const set = new Set<string>();
    for (const col of columns) {
      if (col.readOnly) continue;
      if (col.computed) continue;
      if (editable && col.editable !== false) {
        set.add(col.field);
      } else if (col.editable) {
        set.add(col.field);
      }
    }
    return set;
  }, [columns, editable]);

  const isColumnEditable = useCallback(
    (field: string) => editableColumnSet.has(field),
    [editableColumnSet],
  );

  const getRowByKey = useCallback(
    (key: string): Record<string, unknown> | undefined => {
      return data.find(
        (row) => String((row as Record<string, unknown>)[rowKey]) === key,
      ) as Record<string, unknown> | undefined;
    },
    [data, rowKey],
  );

  const startEditing = useCallback(
    (rk: string, field: string) => {
      if (!editableColumnSet.has(field)) return;
      setEditingCell({ rowKey: rk, field });
      setDirtyRows((prev) => {
        if (prev.has(rk)) return prev;
        const row = getRowByKey(rk);
        if (!row) return prev;
        const next = new Map(prev);
        next.set(rk, {
          original: { ...row },
          changes: {},
          errors: new Map(),
          saving: false,
        });
        return next;
      });
    },
    [editableColumnSet, getRowByKey],
  );

  const cancelEditing = useCallback(() => {
    setEditingCell(null);
  }, []);

  const setCellValue = useCallback(
    (rk: string, field: string, value: unknown) => {
      if (!editableColumnSet.has(field)) return;
      setDirtyRows((prev) => {
        const existing = prev.get(rk);
        const row = existing?.original ?? getRowByKey(rk);
        if (!row) return prev;

        const next = new Map(prev);
        const editState = existing ?? {
          original: { ...row },
          changes: {},
          errors: new Map(),
          saving: false,
        };

        const originalValue = editState.original[field];
        const newChanges = { ...editState.changes };

        if (originalValue === value) {
          delete newChanges[field];
        } else {
          newChanges[field] = value;
        }

        if (Object.keys(newChanges).length === 0) {
          next.delete(rk);
        } else {
          next.set(rk, {
            ...editState,
            changes: newChanges,
            errors: new Map(editState.errors),
          });
        }
        return next;
      });
    },
    [editableColumnSet, getRowByKey],
  );

  const validateCell = useCallback(
    async (
      field: string,
      value: unknown,
      row: Record<string, unknown>,
    ): Promise<string | null> => {
      const col = columns.find((c) => c.field === field);
      if (!col?.validate) return null;
      return col.validate(value, row);
    },
    [columns],
  );

  const commitCell = useCallback(async () => {
    if (!editingCell) return;
    const { rowKey: rk, field } = editingCell;
    const editState = dirtyRows.get(rk);
    if (!editState) {
      setEditingCell(null);
      return;
    }

    const value =
      field in editState.changes
        ? editState.changes[field]
        : editState.original[field];
    const mergedRow = { ...editState.original, ...editState.changes };
    const error = await validateCell(field, value, mergedRow);

    if (error) {
      setDirtyRows((prev) => {
        const state = prev.get(rk);
        if (!state) return prev;
        const next = new Map(prev);
        const errors = new Map(state.errors);
        errors.set(field, error);
        next.set(rk, { ...state, errors });
        return next;
      });
      return;
    }

    setDirtyRows((prev) => {
      const state = prev.get(rk);
      if (!state) return prev;
      const next = new Map(prev);
      const errors = new Map(state.errors);
      errors.delete(field);
      next.set(rk, { ...state, errors });
      return next;
    });

    setEditingCell(null);

    if (autoSave && onRowUpdate && editState.changes[field] !== undefined) {
      const changes = { [field]: editState.changes[field] };
      try {
        await onRowUpdate(editState.original, changes);
        setDirtyRows((prev) => {
          const state = prev.get(rk);
          if (!state) return prev;
          const next = new Map(prev);
          const remaining = { ...state.changes };
          delete remaining[field];
          if (Object.keys(remaining).length === 0) {
            next.delete(rk);
          } else {
            next.set(rk, { ...state, changes: remaining });
          }
          return next;
        });
        refetch();
      } catch {
        // Auto-save failed; keep dirty state for retry
      }
    }
  }, [editingCell, dirtyRows, validateCell, autoSave, onRowUpdate, refetch]);

  const getCellValue = useCallback(
    (rk: string, field: string): unknown => {
      const editState = dirtyRows.get(rk);
      if (editState && field in editState.changes) {
        return editState.changes[field];
      }
      const row = getRowByKey(rk);
      return row ? row[field] : undefined;
    },
    [dirtyRows, getRowByKey],
  );

  const isCellDirty = useCallback(
    (rk: string, field: string): boolean => {
      const editState = dirtyRows.get(rk);
      return editState ? field in editState.changes : false;
    },
    [dirtyRows],
  );

  const getCellError = useCallback(
    (rk: string, field: string): string | null => {
      const editState = dirtyRows.get(rk);
      return editState?.errors.get(field) ?? null;
    },
    [dirtyRows],
  );

  const isRowDirty = useCallback(
    (rk: string): boolean => dirtyRows.has(rk),
    [dirtyRows],
  );

  const getRowChanges = useCallback(
    (rk: string): Record<string, unknown> => dirtyRows.get(rk)?.changes ?? {},
    [dirtyRows],
  );

  const saveRow = useCallback(
    async (rk: string): Promise<boolean> => {
      const editState = dirtyRows.get(rk);
      if (!editState || Object.keys(editState.changes).length === 0)
        return true;

      const mergedRow = { ...editState.original, ...editState.changes };
      const errors = new Map<string, string>();
      for (const [field, value] of Object.entries(editState.changes)) {
        const error = await validateCell(field, value, mergedRow);
        if (error) errors.set(field, error);
      }

      if (errors.size > 0) {
        setDirtyRows((prev) => {
          const state = prev.get(rk);
          if (!state) return prev;
          const next = new Map(prev);
          next.set(rk, { ...state, errors });
          return next;
        });
        return false;
      }

      if (!onRowUpdate) return false;

      setDirtyRows((prev) => {
        const state = prev.get(rk);
        if (!state) return prev;
        const next = new Map(prev);
        next.set(rk, { ...state, saving: true });
        return next;
      });

      optimisticRollbackRef.current.set(rk, editState.original);

      try {
        await onRowUpdate(editState.original, editState.changes);
        setDirtyRows((prev) => {
          const next = new Map(prev);
          next.delete(rk);
          return next;
        });
        optimisticRollbackRef.current.delete(rk);
        refetch();
        return true;
      } catch {
        setDirtyRows((prev) => {
          const state = prev.get(rk);
          if (!state) return prev;
          const next = new Map(prev);
          next.set(rk, { ...state, saving: false });
          return next;
        });
        optimisticRollbackRef.current.delete(rk);
        return false;
      }
    },
    [dirtyRows, validateCell, onRowUpdate, refetch],
  );

  const saveAllDirty = useCallback(async (): Promise<{
    saved: number;
    failed: number;
  }> => {
    const dirtyEntries = Array.from(dirtyRows.entries()).filter(
      ([, state]) => Object.keys(state.changes).length > 0,
    );
    if (dirtyEntries.length === 0) return { saved: 0, failed: 0 };

    if (onBatchSave) {
      const batch = dirtyEntries.map(([, state]) => ({
        row: state.original,
        changes: state.changes,
      }));

      const errorMap = new Map<string, Map<string, string>>();
      for (const [rk, state] of dirtyEntries) {
        const mergedRow = { ...state.original, ...state.changes };
        const rowErrors = new Map<string, string>();
        for (const [field, value] of Object.entries(state.changes)) {
          const error = await validateCell(field, value, mergedRow);
          if (error) rowErrors.set(field, error);
        }
        if (rowErrors.size > 0) errorMap.set(rk, rowErrors);
      }

      if (errorMap.size > 0) {
        setDirtyRows((prev) => {
          const next = new Map(prev);
          for (const [rk, errors] of errorMap) {
            const state = next.get(rk);
            if (state) next.set(rk, { ...state, errors });
          }
          return next;
        });
        return { saved: 0, failed: dirtyEntries.length };
      }

      try {
        await onBatchSave(batch);
        setDirtyRows(new Map());
        refetch();
        return { saved: dirtyEntries.length, failed: 0 };
      } catch {
        return { saved: 0, failed: dirtyEntries.length };
      }
    }

    let saved = 0;
    let failed = 0;
    for (const [rk] of dirtyEntries) {
      const success = await saveRow(rk);
      if (success) saved++;
      else failed++;
    }
    return { saved, failed };
  }, [dirtyRows, onBatchSave, validateCell, saveRow, refetch]);

  const discardRow = useCallback((rk: string) => {
    setDirtyRows((prev) => {
      const next = new Map(prev);
      next.delete(rk);
      return next;
    });
    setEditingCell((prev) => (prev?.rowKey === rk ? null : prev));
  }, []);

  const discardAll = useCallback(() => {
    setDirtyRows(new Map());
    setEditingCell(null);
  }, []);

  const isDirty = dirtyRows.size > 0;
  const dirtyRowCount = dirtyRows.size;

  return {
    editing: {
      editingCell,
      dirtyRows,
      isDirty,
      dirtyRowCount,
      startEditing,
      cancelEditing,
      setCellValue,
      commitCell,
      getCellValue,
      isCellDirty,
      getCellError,
      isRowDirty,
      getRowChanges,
      saveRow,
      saveAllDirty,
      discardRow,
      discardAll,
      isColumnEditable,
    },
    editableColumnSet,
    editingCell,
    startEditing,
    cancelEditing,
  };
}
