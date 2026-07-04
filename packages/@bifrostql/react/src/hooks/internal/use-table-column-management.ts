import { useCallback, useMemo, useState } from 'react';
import {
  DEFAULT_COLUMN_WIDTH,
  estimateColumnWidth,
} from '../../utils/table-client-ops';
import {
  readColumnPresetsFromLocalStorage,
  writeColumnPresetsToLocalStorage,
} from '../../utils/table-storage';
import type {
  ColumnConfig,
  ColumnManagementState,
  ColumnPreset,
  LocalStorageConfig,
  PinPosition,
} from '../use-bifrost-table.types';

export interface UseTableColumnManagementOptions {
  columns: ColumnConfig[];
  data: Record<string, unknown>[];
  localStorageConfig: LocalStorageConfig | undefined;
}

export interface UseTableColumnManagementResult {
  columnManagement: ColumnManagementState;
  visibleColumns: string[];
  columnOrder: string[];
}

/**
 * Owns column visibility, ordering, widths, pinning, and layout presets.
 */
export function useTableColumnManagement({
  columns,
  data,
  localStorageConfig,
}: UseTableColumnManagementOptions): UseTableColumnManagementResult {
  const defaultColumnFields = useMemo(
    () => columns.map((c) => c.field),
    [columns],
  );
  const defaultColumnWidths = useMemo(() => {
    const widths: Record<string, number> = {};
    for (const col of columns) {
      widths[col.field] = col.width ?? DEFAULT_COLUMN_WIDTH;
    }
    return widths;
  }, [columns]);

  const [visibleColumns, setVisibleColumns] = useState<string[]>(() =>
    columns.map((c) => c.field),
  );
  const [columnOrder, setColumnOrder] = useState<string[]>(() =>
    columns.map((c) => c.field),
  );
  const [columnWidths, setColumnWidths] = useState<Record<string, number>>(
    () => ({ ...defaultColumnWidths }),
  );
  const [pinnedColumns, setPinnedColumns] = useState<
    Record<string, PinPosition>
  >(() => ({}));
  const initialColumnPresets = useMemo(() => {
    if (!localStorageConfig?.key) return [];
    return readColumnPresetsFromLocalStorage(localStorageConfig.key);
    // Only read localStorage on mount
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  const [columnPresets, setColumnPresets] =
    useState<ColumnPreset[]>(initialColumnPresets);

  const toggleColumn = useCallback((field: string) => {
    setVisibleColumns((prev) =>
      prev.includes(field) ? prev.filter((f) => f !== field) : [...prev, field],
    );
  }, []);

  const showColumn = useCallback((field: string) => {
    setVisibleColumns((prev) =>
      prev.includes(field) ? prev : [...prev, field],
    );
  }, []);

  const hideColumn = useCallback((field: string) => {
    setVisibleColumns((prev) => prev.filter((f) => f !== field));
  }, []);

  const showAllColumns = useCallback(() => {
    setVisibleColumns([...defaultColumnFields]);
  }, [defaultColumnFields]);

  const reorderColumn = useCallback((from: number, to: number) => {
    setColumnOrder((prev) => {
      if (from < 0 || from >= prev.length || to < 0 || to >= prev.length) {
        return prev;
      }
      const next = [...prev];
      const [moved] = next.splice(from, 1);
      next.splice(to, 0, moved);
      return next;
    });
  }, []);

  const resizeColumn = useCallback((field: string, width: number) => {
    const clamped = Math.max(30, width);
    setColumnWidths((prev) => ({ ...prev, [field]: clamped }));
  }, []);

  const autoFitColumn = useCallback(
    (field: string) => {
      const col = columns.find((c) => c.field === field);
      if (!col) return;
      const width = estimateColumnWidth(field, col.header, data);
      setColumnWidths((prev) => ({ ...prev, [field]: width }));
    },
    [columns, data],
  );

  const autoFitAllColumns = useCallback(() => {
    const widths: Record<string, number> = {};
    for (const col of columns) {
      widths[col.field] = estimateColumnWidth(col.field, col.header, data);
    }
    setColumnWidths(widths);
  }, [columns, data]);

  const pinColumn = useCallback((field: string, position: PinPosition) => {
    setPinnedColumns((prev) => {
      if (position === null) {
        const next = { ...prev };
        delete next[field];
        return next;
      }
      return { ...prev, [field]: position };
    });
  }, []);

  const unpinColumn = useCallback((field: string) => {
    setPinnedColumns((prev) => {
      if (!(field in prev)) return prev;
      const next = { ...prev };
      delete next[field];
      return next;
    });
  }, []);

  const saveColumnPreset = useCallback(
    (name: string) => {
      setColumnPresets((prev) => {
        const preset: ColumnPreset = {
          name,
          visibleColumns: [...visibleColumns],
          columnOrder: [...columnOrder],
          columnWidths: { ...columnWidths },
          pinnedColumns: { ...pinnedColumns },
        };
        const existing = prev.findIndex((p) => p.name === name);
        const updated =
          existing >= 0
            ? prev.map((p, i) => (i === existing ? preset : p))
            : [...prev, preset];
        if (localStorageConfig?.key) {
          writeColumnPresetsToLocalStorage(localStorageConfig.key, updated);
        }
        return updated;
      });
    },
    [
      visibleColumns,
      columnOrder,
      columnWidths,
      pinnedColumns,
      localStorageConfig?.key,
    ],
  );

  const loadColumnPreset = useCallback(
    (name: string) => {
      const preset = columnPresets.find((p) => p.name === name);
      if (!preset) return;
      setVisibleColumns([...preset.visibleColumns]);
      setColumnOrder([...preset.columnOrder]);
      setColumnWidths({ ...preset.columnWidths });
      setPinnedColumns({ ...preset.pinnedColumns });
    },
    [columnPresets],
  );

  const deleteColumnPreset = useCallback(
    (name: string) => {
      setColumnPresets((prev) => {
        const updated = prev.filter((p) => p.name !== name);
        if (localStorageConfig?.key) {
          writeColumnPresetsToLocalStorage(localStorageConfig.key, updated);
        }
        return updated;
      });
    },
    [localStorageConfig?.key],
  );

  const resetColumns = useCallback(() => {
    setVisibleColumns([...defaultColumnFields]);
    setColumnOrder([...defaultColumnFields]);
    setColumnWidths({ ...defaultColumnWidths });
    setPinnedColumns({});
  }, [defaultColumnFields, defaultColumnWidths]);

  return {
    columnManagement: {
      visibleColumns,
      toggleColumn,
      showColumn,
      hideColumn,
      showAllColumns,
      columnOrder,
      reorderColumn,
      columnWidths,
      resizeColumn,
      autoFitColumn,
      autoFitAllColumns,
      pinnedColumns,
      pinColumn,
      unpinColumn,
      presets: columnPresets,
      savePreset: saveColumnPreset,
      loadPreset: loadColumnPreset,
      deletePreset: deleteColumnPreset,
      resetColumns,
    },
    visibleColumns,
    columnOrder,
  };
}
