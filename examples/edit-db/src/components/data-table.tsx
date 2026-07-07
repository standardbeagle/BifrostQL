import { memo, useState, useEffect, useCallback, useMemo, useRef } from 'react';
import {
    ColumnDef,
    ColumnFiltersState,
    ColumnSizingState,
    Row,
    RowSelectionState,
    SortingState,
    VisibilityState,
    flexRender,
    getCoreRowModel,
    useReactTable,
} from '@tanstack/react-table';
import {
    Table,
    TableBody,
    TableCell,
    TableHead,
    TableHeader,
    TableRow,
    TABLE_HEADER_HEIGHT,
    TABLE_ROW_HEIGHT,
} from '@/components/ui/table';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import {
    Select,
    SelectContent,
    SelectItem,
    SelectTrigger,
    SelectValue,
} from '@/components/ui/select';
import {
    DropdownMenu,
    DropdownMenuCheckboxItem,
    DropdownMenuContent,
    DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu';
import {
    ChevronDown,
    ChevronLeft,
    ChevronRight,
    ChevronsLeft,
    ChevronsRight,
    Columns3,
    FilterX,
    PanelRight,
    Rows3,
    Trash2,
} from 'lucide-react';
import { RowActions } from './row-actions';
import { rowIdOf, pkFilterFor, type PkFilter } from '@/lib/row-id';

const COL_MIN_WIDTH = 60;
const COL_MAX_AUTO_WIDTH = 450;
const COL_DEFAULT_WIDTH = 150;
const COL_SIZING_STORAGE_PREFIX = 'bifrost-col-sizes:';

function loadColumnSizing(tableName: string): ColumnSizingState {
    try {
        const raw = localStorage.getItem(COL_SIZING_STORAGE_PREFIX + tableName);
        if (!raw) return {};
        return sanitizeColumnSizing(JSON.parse(raw));
    } catch {
        return {};
    }
}

function sanitizeColumnSizing(value: unknown): ColumnSizingState {
    if (typeof value !== 'object' || value === null || Array.isArray(value)) {
        return {};
    }

    const sizing: ColumnSizingState = {};
    for (const [columnId, width] of Object.entries(value)) {
        if (typeof width !== 'number' || !Number.isFinite(width)) continue;
        sizing[columnId] = Math.min(Math.max(width, COL_MIN_WIDTH), COL_MAX_AUTO_WIDTH);
    }
    return sizing;
}

function saveColumnSizing(tableName: string, sizing: ColumnSizingState): void {
    try {
        if (Object.keys(sizing).length === 0) {
            localStorage.removeItem(COL_SIZING_STORAGE_PREFIX + tableName);
        } else {
            localStorage.setItem(COL_SIZING_STORAGE_PREFIX + tableName, JSON.stringify(sizing));
        }
    } catch {
        // storage full or unavailable
    }
}

/**
 * Props for the DataTable component.
 * @interface DataTableProps
 * @template TData - The type of data rows displayed in the table
 */
interface DataTableProps<TData> {
    /** Column definitions for the table */
    columns: ColumnDef<TData, unknown>[];
    /** Data rows to display */
    data: TData[];
    /** Table name for persisting column sizing to localStorage */
    tableName?: string;
    /** Total number of pages */
    pageCount: number;
    /** Current page index (0-based) */
    pageIndex: number;
    /** Number of rows per page */
    pageSize: number;
    /** Current sorting state */
    sorting: SortingState;
    /** Active column filters */
    columnFilters: ColumnFiltersState;
    /** Whether data is currently loading */
    loading?: boolean;
    /** Enable row selection checkboxes */
    selectable?: boolean;
    /**
     * Primary key column names in declaration order.
     * - Single-column PK: `['id']` (or whichever column).
     * - Composite PK: multiple entries — the row id becomes a composite-encoded string.
     * - Empty / omitted: synthetic `row-${index}` ids; edit/delete actions are disabled.
     */
    primaryKeys?: string[];
    /** Currently selected row ID — must be a value produced by `rowIdOf` (composite-encoded). */
    selectedRowId?: string | null;
    /** Callback when a row is selected */
    onRowSelect?: (rowId: string | null) => void;
    /** Callback when sorting changes */
    onSortingChange: (sorting: SortingState) => void;
    /** Callback when column filters change */
    onColumnFiltersChange: (filters: ColumnFiltersState) => void;
    /** Callback when page index changes */
    onPageIndexChange: (pageIndex: number) => void;
    /** Callback when page size changes */
    onPageSizeChange: (pageSize: number) => void;
    /** Callback when edit action is triggered for a row */
    onEditRow?: (pk: PkFilter) => void;
    /** Callback when delete action is triggered for a row */
    onDeleteRow?: (pk: PkFilter) => void;
    /** Callback when deleting multiple selected rows */
    onDeleteSelected?: (pks: PkFilter[]) => void;
    /**
     * Whether parent/child drill-down ("stacking") mode is active. When the
     * companion `onToggleStacking` is supplied, a graphical toggle renders next
     * to the Columns selector so the user can switch between stacked drill-down
     * and a flat standard grid.
     */
    stackingEnabled?: boolean;
    /** Toggle stacking mode on/off. When omitted, the toggle is not rendered. */
    onToggleStacking?: (next: boolean) => void;
}

const PAGE_SIZE_OPTIONS = [10, 20, 30, 50, 100];
const FIT_SENTINEL = -1;

/**
 * DataTable component - A comprehensive data table with sorting, filtering,
 * pagination, and column resizing capabilities.
 * 
 * Built on top of TanStack Table (@tanstack/react-table) with a Tailwind CSS
 * interface. Supports row selection, inline actions, and persistent column sizing.
 * 
 * @example
 * ```tsx
 * <DataTable
 *   columns={columns}
 *   data={rows}
 *   pageCount={10}
 *   pageIndex={0}
 *   pageSize={20}
 *   sorting={[{ id: 'name', desc: false }]}
 *   columnFilters={[]}
 *   onSortingChange={setSorting}
 *   onColumnFiltersChange={setFilters}
 *   onPageIndexChange={setPageIndex}
 *   onPageSizeChange={setPageSize}
 * />
 * ```
 * 
 * @template TData - The type of data rows
 * @param props - DataTable configuration props
 * @returns React element containing the data table interface
 */
export function DataTable<TData>({
    columns,
    data,
    tableName,
    pageCount,
    pageIndex,
    pageSize,
    sorting,
    columnFilters,
    loading,
    selectable = false,
    primaryKeys = [],
    selectedRowId,
    onRowSelect,
    onSortingChange,
    onColumnFiltersChange,
    onPageIndexChange,
    onPageSizeChange,
    onEditRow,
    onDeleteRow,
    onDeleteSelected,
    stackingEnabled,
    onToggleStacking,
}: DataTableProps<TData>) {
    const [hoveredRow, setHoveredRow] = useState<{ rowId: string; el: HTMLElement } | null>(null);
    const dismissTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

    const hoverRow = useCallback((rowId: string, el: HTMLElement) => {
        if (dismissTimer.current) { clearTimeout(dismissTimer.current); dismissTimer.current = null; }
        setHoveredRow({ rowId, el });
    }, []);

    const scheduleDismiss = useCallback(() => {
        if (dismissTimer.current) clearTimeout(dismissTimer.current);
        dismissTimer.current = setTimeout(() => setHoveredRow(null), 150);
    }, []);

    const cancelDismiss = useCallback(() => {
        if (dismissTimer.current) { clearTimeout(dismissTimer.current); dismissTimer.current = null; }
    }, []);

    // Touch: open the row actions on a long-press (hold) instead of hover, which
    // doesn't exist on touch. A finger move (scroll) cancels the press; a fired
    // press suppresses the click that follows the lift so it doesn't also select.
    const pressTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
    const pressMoved = useRef(false);
    const suppressClick = useRef(false);
    const LONG_PRESS_MS = 500;

    const cancelPress = useCallback(() => {
        if (pressTimer.current) { clearTimeout(pressTimer.current); pressTimer.current = null; }
    }, []);

    const startPress = useCallback((rowId: string, el: HTMLElement, e: React.PointerEvent) => {
        if (e.pointerType !== 'touch') return;
        pressMoved.current = false;
        cancelPress();
        pressTimer.current = setTimeout(() => {
            if (pressMoved.current) return;
            suppressClick.current = true;
            hoverRow(rowId, el);
        }, LONG_PRESS_MS);
    }, [cancelPress, hoverRow]);

    const onPressMove = useCallback(() => {
        pressMoved.current = true;
        cancelPress();
    }, [cancelPress]);
    const [columnVisibility, setColumnVisibility] = useState<VisibilityState>({});
    const [rowSelection, setRowSelection] = useState<RowSelectionState>({});
    const [columnSizing, setColumnSizing] = useState<ColumnSizingState>(() =>
        tableName ? loadColumnSizing(tableName) : {},
    );
    const [fitMode, setFitMode] = useState(true);
    const scrollRef = useRef<HTMLDivElement>(null);

    // Persist column sizing to localStorage, debounced so a live ('onChange')
    // resize drag doesn't write on every mousemove. `skipPersistRef` suppresses
    // the first run after mount/table-switch, which would only write back the
    // sizing just loaded from storage. `pendingSizingRef` carries the latest
    // unsaved state so a table switch or unmount flushes it instead of dropping
    // a resize made less than the debounce window ago.
    const pendingSizingRef = useRef<{ tableName: string; sizing: ColumnSizingState } | null>(null);
    const skipPersistRef = useRef(true);

    useEffect(() => {
        // A pending write for a previous table can no longer be superseded — flush it.
        const stale = pendingSizingRef.current;
        if (stale && stale.tableName !== tableName) {
            pendingSizingRef.current = null;
            saveColumnSizing(stale.tableName, stale.sizing);
        }
        if (!tableName) return;
        if (skipPersistRef.current) {
            skipPersistRef.current = false;
            return;
        }
        pendingSizingRef.current = { tableName, sizing: columnSizing };
        const t = setTimeout(() => {
            pendingSizingRef.current = null;
            saveColumnSizing(tableName, columnSizing);
        }, 300);
        return () => clearTimeout(t);
    }, [tableName, columnSizing]);

    // Flush any still-pending sizing write on unmount rather than dropping it.
    useEffect(() => () => {
        const pending = pendingSizingRef.current;
        if (pending) saveColumnSizing(pending.tableName, pending.sizing);
    }, []);

    // Reset persisted sizing when table changes
    const tableNameRef = useRef(tableName);
    if (tableName && tableName !== tableNameRef.current) {
        tableNameRef.current = tableName;
        skipPersistRef.current = true;
        const restored = loadColumnSizing(tableName);
        setColumnSizing(restored);
    }

    const handleAutoSizeColumn = useCallback((columnId: string) => {
        const container = scrollRef.current;
        if (!container) return;
        const cells = container.querySelectorAll(
            `[data-col-id="${columnId}"]`,
        );
        let maxWidth = COL_MIN_WIDTH;
        cells.forEach((cell) => {
            const width = cell.scrollWidth + 4;
            if (width > maxWidth) maxWidth = width;
        });
        const clamped = Math.min(maxWidth, COL_MAX_AUTO_WIDTH);
        setColumnSizing((prev) => ({ ...prev, [columnId]: clamped }));
    }, []);

    const handleResetColumnWidths = useCallback(() => {
        setColumnSizing({});
    }, []);

    const lastFitSize = useRef(0);

    const computeFitSize = useCallback(() => {
        const el = scrollRef.current;
        if (!el) return 10;
        return Math.max(5, Math.floor((el.clientHeight - TABLE_HEADER_HEIGHT) / TABLE_ROW_HEIGHT));
    }, []);

    // Apply fit on mount and when the container resizes. The resize path is
    // debounced so dragging a splitter/window doesn't fire a burst of queries
    // (each distinct page size is a new query key) — only the settled size runs.
    useEffect(() => {
        if (!fitMode) return;
        const el = scrollRef.current;
        if (!el) return;
        const apply = () => {
            const size = computeFitSize();
            if (size === lastFitSize.current) return;
            lastFitSize.current = size;
            onPageSizeChange(size);
        };
        const raf = requestAnimationFrame(apply);
        let debounce: ReturnType<typeof setTimeout> | null = null;
        const ro = new ResizeObserver(() => {
            if (debounce) clearTimeout(debounce);
            debounce = setTimeout(() => requestAnimationFrame(apply), 200);
        });
        ro.observe(el);
        return () => {
            cancelAnimationFrame(raf);
            if (debounce) clearTimeout(debounce);
            ro.disconnect();
        };
    }, [fitMode, computeFitSize, onPageSizeChange]);

    // Clear selection when data/page changes
    useEffect(() => {
        setRowSelection({});
    }, [data, pageIndex]);

    // Build columns with optional select column. Memoized so the table doesn't
    // see a fresh `columns` identity every render (which forces it to re-derive
    // its column models) — matters once hover/resize churn is in play.
    const allColumns = useMemo<ColumnDef<TData, unknown>[]>(() => {
        const cols: ColumnDef<TData, unknown>[] = [];
        if (selectable) {
            cols.push({
                id: '_select',
                header: ({ table: t }) => (
                    <Checkbox
                        checked={t.getIsAllPageRowsSelected() || (t.getIsSomePageRowsSelected() && 'indeterminate')}
                        onCheckedChange={(value) => t.toggleAllPageRowsSelected(!!value)}
                        aria-label="Select all"
                    />
                ),
                cell: ({ row }) => (
                    <Checkbox
                        checked={row.getIsSelected()}
                        onCheckedChange={(value) => row.toggleSelected(!!value)}
                        onClick={(e) => e.stopPropagation()}
                        aria-label="Select row"
                    />
                ),
                enableSorting: false,
                enableHiding: false,
                enableResizing: false,
                size: 32,
            });
        }
        cols.push(...columns);
        return cols;
    }, [columns, selectable]);

    const table = useReactTable({
        data,
        columns: allColumns,
        pageCount,
        getRowId: (row, index) => rowIdOf(row as Record<string, unknown>, { primaryKeys }, index),
        state: {
            sorting,
            columnFilters,
            pagination: { pageIndex, pageSize },
            columnVisibility,
            rowSelection,
            columnSizing,
        },
        defaultColumn: {
            minSize: COL_MIN_WIDTH,
            size: COL_DEFAULT_WIDTH,
        },
        // 'onChange' keeps live drag feedback. Cheap now that body rows are
        // memoized and read width from <colgroup> (a resize re-renders only the
        // table shell/header/colgroup, not every cell); the localStorage write is
        // debounced below so it doesn't fire per pixel.
        columnResizeMode: 'onChange',
        enableColumnResizing: true,
        onColumnSizingChange: setColumnSizing,
        meta: { onResetColumnWidths: handleResetColumnWidths },
        onSortingChange: (updater) => {
            const next = typeof updater === 'function' ? updater(sorting) : updater;
            onSortingChange(next);
        },
        onPaginationChange: (updater) => {
            const prev = { pageIndex, pageSize };
            const next = typeof updater === 'function' ? updater(prev) : updater;
            if (next.pageSize !== pageSize) {
                onPageSizeChange(next.pageSize);
                onPageIndexChange(0);
            } else {
                onPageIndexChange(next.pageIndex);
            }
        },
        onColumnFiltersChange: (updater) => {
            const next = typeof updater === 'function' ? updater(columnFilters) : updater;
            onColumnFiltersChange(next);
        },
        onColumnVisibilityChange: setColumnVisibility,
        onRowSelectionChange: setRowSelection,
        enableRowSelection: selectable,
        getCoreRowModel: getCoreRowModel(),
        manualSorting: true,
        manualFiltering: true,
        manualPagination: true,
    });

    const selectedCount = Object.keys(rowSelection).length;

    // Signature of the currently-visible columns. Passed to each memoized row so
    // toggling column visibility re-renders the body (otherwise rows keep their
    // old cell set and drift out of alignment with the header under fixed layout).
    const visibleColumnKey = table.getVisibleLeafColumns().map((c) => c.id).join('|');

    const handleDeleteSelected = useCallback(() => {
        if (!onDeleteSelected) return;
        const filters: PkFilter[] = [];
        for (const r of table.getSelectedRowModel().rows) {
            const f = pkFilterFor(r.original as Record<string, unknown>, { primaryKeys });
            if (f) filters.push(f);
        }
        if (filters.length > 0) onDeleteSelected(filters);
    }, [table, onDeleteSelected, primaryKeys]);

    const buildRowPkFilter = useCallback((rowId: string): PkFilter | null => {
        const row = table.getRow(rowId);
        return row ? pkFilterFor(row.original as Record<string, unknown>, { primaryKeys }) : null;
    }, [table, primaryKeys]);

    // Edit/delete by row id — used by keyboard handling on the focused row
    // ('e' edits, Delete/Backspace deletes) so the actions aren't mouse/touch only.
    const editRowById = useCallback((rowId: string) => {
        if (!onEditRow) return;
        const filter = buildRowPkFilter(rowId);
        if (filter) onEditRow(filter);
    }, [onEditRow, buildRowPkFilter]);

    const deleteRowById = useCallback((rowId: string) => {
        if (!onDeleteRow) return;
        const filter = buildRowPkFilter(rowId);
        if (filter) onDeleteRow(filter);
    }, [onDeleteRow, buildRowPkFilter]);

    // Hover-toolbar actions: the same operations pre-bound to the hovered row.
    const handleHoverEdit = useCallback(() => {
        if (hoveredRow) editRowById(hoveredRow.rowId);
    }, [hoveredRow, editRowById]);

    const handleHoverDelete = useCallback(() => {
        if (hoveredRow) deleteRowById(hoveredRow.rowId);
    }, [hoveredRow, deleteRowById]);

    return (
        <div className="flex flex-col w-full min-h-0 flex-1">
            <div className="flex items-center justify-between py-1 px-2">
                <div className="flex items-center gap-2">
                    {columnFilters.length > 0 && (
                        <Button
                            variant="outline"
                            size="sm"
                            onClick={() => table.resetColumnFilters()}
                            title="Clear all column filters"
                        >
                            <FilterX className="size-3.5" />
                            Clear filters
                        </Button>
                    )}
                    {selectedCount > 0 && onDeleteSelected && (
                        <>
                            <span className="text-xs text-muted-foreground">
                                {selectedCount} selected
                            </span>
                            <Button
                                variant="outline"
                                size="sm"
                                onClick={handleDeleteSelected}
                                className="text-destructive hover:text-destructive"
                            >
                                <Trash2 className="size-3.5" />
                                Delete
                            </Button>
                        </>
                    )}
                </div>
                <div className="flex items-center gap-2">
                {onToggleStacking && (
                    <Button
                        variant={stackingEnabled ? 'secondary' : 'outline'}
                        size="sm"
                        role="switch"
                        aria-checked={!!stackingEnabled}
                        onClick={() => onToggleStacking(!stackingEnabled)}
                        title={stackingEnabled
                            ? 'Stacked drill-down active — click for flat grid'
                            : 'Flat grid active — click for stacked drill-down'}
                    >
                        {stackingEnabled
                            ? <PanelRight className="size-3.5" />
                            : <Rows3 className="size-3.5" />}
                        {stackingEnabled ? 'Stacked' : 'Grid'}
                    </Button>
                )}
                <DropdownMenu>
                    <DropdownMenuTrigger asChild>
                        <Button variant="outline" size="sm">
                            <Columns3 className="size-3.5" />
                            Columns
                            <ChevronDown className="size-3.5" />
                        </Button>
                    </DropdownMenuTrigger>
                    <DropdownMenuContent align="end">
                        {table.getAllColumns()
                            .filter((column) => column.getCanHide())
                            .map((column) => {
                                // Prefer the humanized column label (matches every
                                // other surface) over the raw db column id.
                                const meta = column.columnDef.meta as { column?: { label?: string } } | undefined;
                                const label = meta?.column?.label
                                    ?? (typeof column.columnDef.header === 'string' ? column.columnDef.header : column.id);
                                return (
                                    <DropdownMenuCheckboxItem
                                        key={column.id}
                                        checked={column.getIsVisible()}
                                        onCheckedChange={(value) => column.toggleVisibility(!!value)}
                                    >
                                        {label}
                                    </DropdownMenuCheckboxItem>
                                );
                            })}
                    </DropdownMenuContent>
                </DropdownMenu>
                </div>
            </div>
            <div ref={scrollRef} className="flex-1 overflow-auto min-h-0">
                <Table style={{ tableLayout: 'fixed', width: table.getTotalSize() }}>
                    {/* Column widths live on <colgroup> under table-layout:fixed so
                        body cells don't each carry a width style — that keeps the
                        memoized rows independent of column sizing, and a resize only
                        re-renders the colgroup, not every cell. */}
                    <colgroup>
                        {table.getVisibleLeafColumns().map((col) => (
                            <col key={col.id} style={{ width: col.getSize() }} />
                        ))}
                    </colgroup>
                    <TableHeader>
                        {table.getHeaderGroups().map((headerGroup) => (
                            <TableRow key={headerGroup.id}>
                                {headerGroup.headers.map((header) => (
                                    <TableHead
                                        key={header.id}
                                        data-col-id={header.column.id}
                                        className="relative"
                                        aria-sort={
                                            header.column.getIsSorted() === 'asc' ? 'ascending'
                                            : header.column.getIsSorted() === 'desc' ? 'descending'
                                            : header.column.getCanSort() ? 'none' : undefined
                                        }
                                    >
                                        {header.isPlaceholder
                                            ? null
                                            : flexRender(header.column.columnDef.header, header.getContext())}
                                        {header.column.getCanResize() && (
                                            <div
                                                onMouseDown={header.getResizeHandler()}
                                                onTouchStart={header.getResizeHandler()}
                                                onDoubleClick={() => handleAutoSizeColumn(header.column.id)}
                                                className={`absolute top-0 right-0 w-1 h-full cursor-col-resize select-none touch-none opacity-0 hover:opacity-100 transition-opacity ${
                                                    header.column.getIsResizing()
                                                        ? 'bg-primary opacity-100'
                                                        : 'bg-border hover:bg-muted-foreground'
                                                }`}
                                            />
                                        )}
                                    </TableHead>
                                ))}
                            </TableRow>
                        ))}
                    </TableHeader>
                    <TableBody>
                        {loading ? (
                            <TableRow>
                                <TableCell colSpan={allColumns.length} className="h-24 text-center">
                                    Loading...
                                </TableCell>
                            </TableRow>
                        ) : table.getRowModel().rows.length === 0 ? (
                            <TableRow>
                                <TableCell colSpan={allColumns.length} className="h-24 text-center">
                                    No results.
                                </TableCell>
                            </TableRow>
                        ) : (
                            table.getRowModel().rows.map((row) => (
                                <DataTableRow
                                    key={row.id}
                                    row={row}
                                    isSelected={selectedRowId != null && row.id === selectedRowId}
                                    checked={row.getIsSelected()}
                                    visibleKey={visibleColumnKey}
                                    onRowSelect={onRowSelect}
                                    onEditRow={onEditRow ? editRowById : undefined}
                                    onDeleteRow={onDeleteRow ? deleteRowById : undefined}
                                    suppressClick={suppressClick}
                                    hoverRow={hoverRow}
                                    scheduleDismiss={scheduleDismiss}
                                    startPress={startPress}
                                    onPressMove={onPressMove}
                                    cancelPress={cancelPress}
                                />
                            ))
                        )}
                    </TableBody>
                </Table>
                {hoveredRow && (onEditRow || onDeleteRow) && (
                    <RowActions
                        anchorEl={hoveredRow.el}
                        onEdit={onEditRow ? handleHoverEdit : undefined}
                        onDelete={onDeleteRow ? handleHoverDelete : undefined}
                        onMouseEnter={cancelDismiss}
                        onDismiss={scheduleDismiss}
                    />
                )}
            </div>
            <div className="flex items-center justify-between px-2 py-1.5 border-t border-border mt-auto shrink-0">
                <div className="flex items-center gap-2 text-xs text-muted-foreground">
                    <span>Rows per page</span>
                    <Select
                        value={fitMode ? String(FIT_SENTINEL) : String(pageSize)}
                        onValueChange={(val) => {
                            const n = Number(val);
                            if (n === FIT_SENTINEL) {
                                setFitMode(true);
                                onPageSizeChange(computeFitSize());
                            } else {
                                setFitMode(false);
                                onPageSizeChange(n);
                            }
                        }}
                    >
                        <SelectTrigger size="sm" className="w-auto h-7 text-xs" aria-label="Rows per page">
                            <SelectValue>{fitMode ? 'Fit' : pageSize}</SelectValue>
                        </SelectTrigger>
                        <SelectContent>
                            <SelectItem value={String(FIT_SENTINEL)}>Fit</SelectItem>
                            {PAGE_SIZE_OPTIONS.map((size) => (
                                <SelectItem key={size} value={String(size)}>
                                    {size}
                                </SelectItem>
                            ))}
                        </SelectContent>
                    </Select>
                </div>
                <div className="flex items-center gap-1.5">
                    <span className="text-xs text-muted-foreground mr-2">
                        Page {pageIndex + 1} of {pageCount || 1}
                    </span>
                    <Button
                        variant="outline"
                        size="icon-sm"
                        onClick={() => onPageIndexChange(0)}
                        disabled={pageIndex === 0}
                        aria-label="First page"
                        title="First page"
                    >
                        <ChevronsLeft className="size-4" />
                    </Button>
                    <Button
                        variant="outline"
                        size="icon-sm"
                        onClick={() => onPageIndexChange(pageIndex - 1)}
                        disabled={!table.getCanPreviousPage()}
                        aria-label="Previous page"
                        title="Previous page"
                    >
                        <ChevronLeft className="size-4" />
                    </Button>
                    <Button
                        variant="outline"
                        size="icon-sm"
                        onClick={() => onPageIndexChange(pageIndex + 1)}
                        disabled={!table.getCanNextPage()}
                        aria-label="Next page"
                        title="Next page"
                    >
                        <ChevronRight className="size-4" />
                    </Button>
                    <Button
                        variant="outline"
                        size="icon-sm"
                        onClick={() => onPageIndexChange(pageCount - 1)}
                        disabled={pageIndex >= pageCount - 1}
                        aria-label="Last page"
                        title="Last page"
                    >
                        <ChevronsRight className="size-4" />
                    </Button>
                </div>
            </div>
        </div>
    );
}

interface DataTableRowProps<TData> {
    row: Row<TData>;
    isSelected: boolean;
    /** Checkbox selection state, passed explicitly so the memoized row re-renders
     *  when it toggles (row.getIsSelected() alone wouldn't invalidate the memo). */
    checked: boolean;
    /** Visible-column signature; a change invalidates the memo so the row's cell
     *  set stays in sync with the header when column visibility toggles. */
    visibleKey: string;
    onRowSelect?: (rowId: string | null) => void;
    onEditRow?: (rowId: string) => void;
    onDeleteRow?: (rowId: string) => void;
    suppressClick: React.MutableRefObject<boolean>;
    hoverRow: (rowId: string, el: HTMLElement) => void;
    scheduleDismiss: () => void;
    startPress: (rowId: string, el: HTMLElement, e: React.PointerEvent) => void;
    onPressMove: () => void;
    cancelPress: () => void;
}

/**
 * One data row, memoized so table-level state that doesn't affect a row (mouse
 * hover tracking, column resize) doesn't re-render every row's cells. Keyboard
 * users can act on a focused row: Enter/Space selects, `e` edits, Delete removes
 * — so edit/delete aren't hover/long-press only.
 */
function DataTableRowInner<TData>({
    row,
    isSelected,
    checked,
    onRowSelect,
    onEditRow,
    onDeleteRow,
    suppressClick,
    hoverRow,
    scheduleDismiss,
    startPress,
    onPressMove,
    cancelPress,
}: DataTableRowProps<TData>) {
    const actionable = !!(onRowSelect || onEditRow || onDeleteRow);
    return (
        <TableRow
            data-state={isSelected || checked ? 'selected' : undefined}
            className={onRowSelect ? 'cursor-pointer group/row focus-visible:outline-2 focus-visible:outline-primary' : 'group/row focus-visible:outline-2 focus-visible:outline-primary'}
            tabIndex={actionable ? 0 : undefined}
            onClick={onRowSelect ? () => {
                // A long-press just opened the actions overlay — swallow the
                // click that follows the finger lift so it doesn't also select.
                if (suppressClick.current) { suppressClick.current = false; return; }
                // Don't hijack a text-selection drag as a row click — lets users
                // select/copy cell text without navigating.
                if (window.getSelection()?.toString()) return;
                onRowSelect(isSelected ? null : row.id);
            } : undefined}
            onKeyDown={actionable ? (e) => {
                // Ignore keys originating from an interactive child (link, button,
                // checkbox) so their own handling isn't hijacked.
                if (e.target !== e.currentTarget) return;
                if ((e.key === 'Enter' || e.key === ' ') && onRowSelect) {
                    e.preventDefault();
                    onRowSelect(isSelected ? null : row.id);
                } else if ((e.key === 'e' || e.key === 'E') && onEditRow) {
                    e.preventDefault();
                    onEditRow(row.id);
                } else if ((e.key === 'Delete' || e.key === 'Backspace') && onDeleteRow) {
                    e.preventDefault();
                    onDeleteRow(row.id);
                }
            } : undefined}
            onFocus={(e) => { if (e.target === e.currentTarget) hoverRow(row.id, e.currentTarget); }}
            onBlur={(e) => { if (!e.currentTarget.contains(e.relatedTarget as Node | null)) scheduleDismiss(); }}
            onPointerEnter={(e) => { if (e.pointerType === 'mouse') hoverRow(row.id, e.currentTarget); }}
            onPointerLeave={(e) => { if (e.pointerType === 'mouse') scheduleDismiss(); }}
            onPointerDown={(e) => startPress(row.id, e.currentTarget, e)}
            onPointerMove={onPressMove}
            onPointerUp={cancelPress}
            onPointerCancel={cancelPress}
        >
            {row.getVisibleCells().map((cell) => (
                <TableCell key={cell.id} data-col-id={cell.column.id}>
                    {flexRender(cell.column.columnDef.cell, cell.getContext())}
                </TableCell>
            ))}
        </TableRow>
    );
}

const DataTableRow = memo(DataTableRowInner) as typeof DataTableRowInner;
