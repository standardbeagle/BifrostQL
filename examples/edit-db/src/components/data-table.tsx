import { memo, useState, useEffect, useCallback, useMemo, useRef } from 'react';
import {
    ColumnDef,
    ColumnFiltersState,
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
import { ExportButton } from './export-button';
import type { ExportRunner } from '@/lib/export';
import { rowIdOf, pkFilterFor, type PkFilter } from '@/lib/row-id';
import {
    useColumnSizingPersistence,
    COL_MIN_WIDTH,
    COL_MAX_AUTO_WIDTH,
    COL_DEFAULT_WIDTH,
} from '@/hooks/useColumnSizingPersistence';
import { useRowHoverActions } from '@/hooks/useRowHoverActions';
import { useFitMode, FIT_SENTINEL } from '@/hooks/useFitMode';

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
    /**
     * Full-result-set export runner (pages every matching row for the current
     * filters/sort). When supplied, an Export control renders in the toolbar.
     */
    exportRows?: ExportRunner;
    /** Total matching rows — drives the export's above-cap confirmation. */
    totalRows?: number;
}

const PAGE_SIZE_OPTIONS = [10, 20, 30, 50, 100];

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
    exportRows,
    totalRows,
}: DataTableProps<TData>) {
    const {
        hoveredRow,
        hoverRow,
        scheduleDismiss,
        cancelDismiss,
        suppressClick,
        startPress,
        onPressMove,
        cancelPress,
    } = useRowHoverActions();
    const [columnVisibility, setColumnVisibility] = useState<VisibilityState>({});
    const [rowSelection, setRowSelection] = useState<RowSelectionState>({});
    const { columnSizing, setColumnSizing } = useColumnSizingPersistence(tableName);
    const scrollRef = useRef<HTMLDivElement>(null);
    const { fitMode, setFitMode, computeFitSize } = useFitMode(scrollRef, onPageSizeChange);

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
    }, [setColumnSizing]);

    const handleResetColumnWidths = useCallback(() => {
        setColumnSizing({});
    }, [setColumnSizing]);

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
                {exportRows && (
                    <ExportButton
                        exportRows={exportRows}
                        total={totalRows ?? 0}
                        tableName={tableName ?? 'export'}
                    />
                )}
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
