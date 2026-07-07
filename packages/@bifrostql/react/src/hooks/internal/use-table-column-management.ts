import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
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
  ColumnManagementConfig,
  ColumnManagementState,
  ColumnPreset,
  LocalStorageConfig,
  PinPosition,
} from '../use-bifrost-table.types';

export interface UseTableColumnManagementOptions {
  columns: ColumnConfig[];
  data: Record<string, unknown>[];
  localStorageConfig: LocalStorageConfig | undefined;
  /**
   * Capability gates. An omitted config (or omitted flag) leaves every
   * operation enabled — disabling a flag turns the matching mutator into a
   * no-op so a host can present read-only column controls.
   */
  config: ColumnManagementConfig | undefined;
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
  config,
}: UseTableColumnManagementOptions): UseTableColumnManagementResult {
  const hideable = config?.hideable ?? true;
  const reorderable = config?.reorderable ?? true;
  const resizable = config?.resizable ?? true;
  const freezable = config?.freezable ?? true;
  const defaultColumnFields = useMemo(
    () => columns.map((c) => c.field),
    [columns],
  );
  const previousColumnFieldsRef = useRef(defaultColumnFields);
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

  useEffect(() => {
    const fieldSet = new Set(defaultColumnFields);
    const previousFieldSet = new Set(previousColumnFieldsRef.current);
    const newFields = defaultColumnFields.filter(
      (field) => !previousFieldSet.has(field),
    );

    setVisibleColumns((prev) => {
      const next = [...prev.filter((field) => fieldSet.has(field))];
      for (const field of newFields) {
        if (!next.includes(field)) next.push(field);
      }
      return arraysEqual(prev, next) ? prev : next;
    });

    setColumnOrder((prev) => {
      const next = [
        ...prev.filter((field) => fieldSet.has(field)),
        ...newFields.filter((field) => !prev.includes(field)),
      ];
      return arraysEqual(prev, next) ? prev : next;
    });

    setColumnWidths((prev) => {
      let changed = false;
      const next: Record<string, number> = {};
      for (const field of defaultColumnFields) {
        next[field] = prev[field] ?? defaultColumnWidths[field];
        if (next[field] !== prev[field]) changed = true;
      }
      for (const field of Object.keys(prev)) {
        if (!fieldSet.has(field)) {
          changed = true;
          break;
        }
      }
      return changed ? next : prev;
    });

    setPinnedColumns((prev) => {
      let changed = false;
      const next: Record<string, PinPosition> = {};
      for (const [field, position] of Object.entries(prev)) {
        if (fieldSet.has(field)) {
          next[field] = position;
        } else {
          changed = true;
        }
      }
      return changed ? next : prev;
    });

    previousColumnFieldsRef.current = defaultColumnFields;
  }, [defaultColumnFields, defaultColumnWidths]);

  const toggleColumn = useCallback(
    (field: string) => {
      if (!hideable) return;
      setVisibleColumns((prev) =>
        prev.includes(field)
          ? prev.filter((f) => f !== field)
          : [...prev, field],
      );
    },
    [hideable],
  );

  const showColumn = useCallback(
    (field: string) => {
      if (!hideable) return;
      setVisibleColumns((prev) =>
        prev.includes(field) ? prev : [...prev, field],
      );
    },
    [hideable],
  );

  const hideColumn = useCallback(
    (field: string) => {
      if (!hideable) return;
      setVisibleColumns((prev) => prev.filter((f) => f !== field));
    },
    [hideable],
  );

  const showAllColumns = useCallback(() => {
    if (!hideable) return;
    setVisibleColumns([...defaultColumnFields]);
  }, [hideable, defaultColumnFields]);

  const reorderColumn = useCallback(
    (from: number, to: number) => {
      if (!reorderable) return;
      setColumnOrder((prev) => {
        if (from < 0 || from >= prev.length || to < 0 || to >= prev.length) {
          return prev;
        }
        const next = [...prev];
        const [moved] = next.splice(from, 1);
        next.splice(to, 0, moved);
        return next;
      });
    },
    [reorderable],
  );

  const resizeColumn = useCallback(
    (field: string, width: number) => {
      if (!resizable) return;
      const clamped = Math.max(30, width);
      setColumnWidths((prev) => ({ ...prev, [field]: clamped }));
    },
    [resizable],
  );

  const autoFitColumn = useCallback(
    (field: string) => {
      if (!resizable) return;
      const col = columns.find((c) => c.field === field);
      if (!col) return;
      const width = estimateColumnWidth(field, col.header, data);
      setColumnWidths((prev) => ({ ...prev, [field]: width }));
    },
    [resizable, columns, data],
  );

  const autoFitAllColumns = useCallback(() => {
    if (!resizable) return;
    const widths: Record<string, number> = {};
    for (const col of columns) {
      widths[col.field] = estimateColumnWidth(col.field, col.header, data);
    }
    setColumnWidths(widths);
  }, [resizable, columns, data]);

  const pinColumn = useCallback(
    (field: string, position: PinPosition) => {
      if (!freezable) return;
      setPinnedColumns((prev) => {
        if (position === null) {
          const next = { ...prev };
          delete next[field];
          return next;
        }
        return { ...prev, [field]: position };
      });
    },
    [freezable],
  );

  const unpinColumn = useCallback(
    (field: string) => {
      if (!freezable) return;
      setPinnedColumns((prev) => {
        if (!(field in prev)) return prev;
        const next = { ...prev };
        delete next[field];
        return next;
      });
    },
    [freezable],
  );

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
      const fieldSet = new Set(defaultColumnFields);
      setVisibleColumns(
        preset.visibleColumns.filter((field) => fieldSet.has(field)),
      );
      setColumnOrder([
        ...preset.columnOrder.filter((field) => fieldSet.has(field)),
        ...defaultColumnFields.filter(
          (field) => !preset.columnOrder.includes(field),
        ),
      ]);
      const widths: Record<string, number> = {};
      for (const field of defaultColumnFields) {
        widths[field] = preset.columnWidths[field] ?? defaultColumnWidths[field];
      }
      setColumnWidths(widths);
      const pinned: Record<string, PinPosition> = {};
      for (const [field, position] of Object.entries(preset.pinnedColumns)) {
        if (fieldSet.has(field)) {
          pinned[field] = position;
        }
      }
      setPinnedColumns(pinned);
    },
    [columnPresets, defaultColumnFields, defaultColumnWidths],
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

function arraysEqual(left: string[], right: string[]): boolean {
  return (
    left.length === right.length &&
    left.every((value, index) => value === right[index])
  );
}
