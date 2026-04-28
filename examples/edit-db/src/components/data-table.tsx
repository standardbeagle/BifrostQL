import { useState, useEffect, useCallback, useRef } from 'react';
import {
    ColumnDef,
    ColumnFiltersState,
    ColumnSizingState,
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
        return JSON.parse(raw) as ColumnSizingState;
    } catch {
        return {};
    }
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
    const [columnVisibility, setColumnVisibility] = useState<VisibilityState>({});
    const [rowSelection, setRowSelection] = useState<RowSelectionState>({});
    const [columnSizing, setColumnSizing] = useState<ColumnSizingState>(() =>
        tableName ? loadColumnSizing(tableName) : {},
    );
    const [fitMode, setFitMode] = useState(true);
    const scrollRef = useRef<HTMLDivElement>(null);

    // Persist column sizing to localStorage when it changes
    useEffect(() => {
        if (tableName) saveColumnSizing(tableName, columnSizing);
    }, [tableName, columnSizing]);

    // Reset persisted sizing when table changes
    const tableNameRef = useRef(tableName);
    if (tableName && tableName !== tableNameRef.current) {
        tableNameRef.current = tableName;
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

    // Apply fit on mount and when the container resizes
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
        const ro = new ResizeObserver(() => requestAnimationFrame(apply));
        ro.observe(el);
        return () => { cancelAnimationFrame(raf); ro.disconnect(); };
    }, [fitMode, computeFitSize, onPageSizeChange]);

    // Clear selection when data/page changes
    useEffect(() => {
        setRowSelection({});
    }, [data, pageIndex]);

    // Build columns with optional select and delete columns
    const allColumns: ColumnDef<TData, unknown>[] = [];

    if (selectable) {
        allColumns.push({
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

    allColumns.push(...columns);

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

    const handleHoverEdit = useCallback(() => {
        if (!hoveredRow || !onEditRow) return;
        const filter = buildRowPkFilter(hoveredRow.rowId);
        if (filter) onEditRow(filter);
    }, [hoveredRow, onEditRow, buildRowPkFilter]);

    const handleHoverDelete = useCallback(() => {
        if (!hoveredRow || !onDeleteRow) return;
        const filter = buildRowPkFilter(hoveredRow.rowId);
        if (filter) onDeleteRow(filter);
    }, [hoveredRow, onDeleteRow, buildRowPkFilter]);

    return (
        <div className="flex flex-col w-full min-h-0 flex-1">
            <div className="flex items-center justify-between py-1 px-2">
                <div className="flex items-center gap-2">
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
                            .map((column) => (
                                <DropdownMenuCheckboxItem
                                    key={column.id}
                                    checked={column.getIsVisible()}
                                    onCheckedChange={(value) => column.toggleVisibility(!!value)}
                                >
                                    {column.id}
                                </DropdownMenuCheckboxItem>
                            ))}
                    </DropdownMenuContent>
                </DropdownMenu>
            </div>
            <div ref={scrollRef} className="flex-1 overflow-auto min-h-0">
                <Table style={{ width: table.getTotalSize() }}>
                    <TableHeader>
                        {table.getHeaderGroups().map((headerGroup) => (
                            <TableRow key={headerGroup.id}>
                                {headerGroup.headers.map((header) => (
                                    <TableHead
                                        key={header.id}
                                        data-col-id={header.column.id}
                                        className="relative"
                                        style={{ width: header.getSize() }}
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
                            table.getRowModel().rows.map((row) => {
                                const isSelected = selectedRowId != null && row.id === selectedRowId;
                                return (
                                    <TableRow
                                        key={row.id}
                                        data-state={isSelected ? 'selected' : row.getIsSelected() ? 'selected' : undefined}
                                        className={onRowSelect ? 'cursor-pointer group/row' : 'group/row'}
                                        onClick={onRowSelect ? () => onRowSelect(isSelected ? null : row.id) : undefined}
                                        onMouseEnter={(e) => hoverRow(row.id, e.currentTarget)}
                                        onMouseLeave={scheduleDismiss}
                                    >
                                        {row.getVisibleCells().map((cell) => (
                                            <TableCell
                                                key={cell.id}
                                                data-col-id={cell.column.id}
                                                style={{ width: cell.column.getSize() }}
                                            >
                                                {flexRender(cell.column.columnDef.cell, cell.getContext())}
                                            </TableCell>
                                        ))}
                                    </TableRow>
                                );
                            })
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
