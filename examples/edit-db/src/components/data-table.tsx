import { useState, useEffect, useCallback } from 'react';
import {
    ColumnDef,
    ColumnFiltersState,
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
    Trash2,
} from 'lucide-react';

interface DataTableProps<TData> {
    columns: ColumnDef<TData, unknown>[];
    data: TData[];
    pageCount: number;
    pageIndex: number;
    pageSize: number;
    sorting: SortingState;
    columnFilters: ColumnFiltersState;
    loading?: boolean;
    selectable?: boolean;
    rowIdField?: string;
    selectedRowId?: string | null;
    onRowSelect?: (rowId: string | null) => void;
    onSortingChange: (sorting: SortingState) => void;
    onColumnFiltersChange: (filters: ColumnFiltersState) => void;
    onPageIndexChange: (pageIndex: number) => void;
    onPageSizeChange: (pageSize: number) => void;
    onDeleteSelected?: (pks: string[]) => void;
}

const PAGE_SIZE_OPTIONS = [10, 20, 30, 50, 100];
const ROW_HEIGHT_ESTIMATE = 41;
const CHROME_HEIGHT = 160;

function getScreenPageSize(): number {
    const available = window.innerHeight - CHROME_HEIGHT;
    const rows = Math.floor(available / ROW_HEIGHT_ESTIMATE);
    const clamped = Math.max(5, rows);
    return PAGE_SIZE_OPTIONS.reduce((prev, curr) =>
        Math.abs(curr - clamped) < Math.abs(prev - clamped) ? curr : prev
    );
}

export function DataTable<TData>({
    columns,
    data,
    pageCount,
    pageIndex,
    pageSize,
    sorting,
    columnFilters,
    loading,
    selectable = false,
    rowIdField = 'id',
    selectedRowId,
    onRowSelect,
    onSortingChange,
    onColumnFiltersChange,
    onPageIndexChange,
    onPageSizeChange,
    onDeleteSelected,
}: DataTableProps<TData>) {
    const [columnVisibility, setColumnVisibility] = useState<VisibilityState>({});
    const [rowSelection, setRowSelection] = useState<RowSelectionState>({});
    const [initialized, setInitialized] = useState(false);

    useEffect(() => {
        if (!initialized) {
            onPageSizeChange(getScreenPageSize());
            setInitialized(true);
        }
    }, [initialized, onPageSizeChange]);

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
        });
    }

    allColumns.push(...columns);

    const table = useReactTable({
        data,
        columns: allColumns,
        pageCount,
        getRowId: (row) => String((row as Record<string, unknown>)?.[rowIdField] ?? ''),
        state: {
            sorting,
            columnFilters,
            pagination: { pageIndex, pageSize },
            columnVisibility,
            rowSelection,
        },
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
        const selectedPks = Object.keys(rowSelection);
        if (selectedPks.length > 0 && onDeleteSelected) {
            onDeleteSelected(selectedPks);
        }
    }, [rowSelection, onDeleteSelected]);

    return (
        <div className="flex flex-col w-full min-h-0 flex-1">
            <div className="flex items-center justify-between py-1.5 px-3">
                <div className="flex items-center gap-2">
                    {selectedCount > 0 && onDeleteSelected && (
                        <>
                            <span className="text-sm text-muted-foreground">
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
            <div className="flex-1 overflow-y-auto min-h-0">
                <Table>
                    <TableHeader>
                        {table.getHeaderGroups().map((headerGroup) => (
                            <TableRow key={headerGroup.id}>
                                {headerGroup.headers.map((header) => (
                                    <TableHead key={header.id}>
                                        {header.isPlaceholder
                                            ? null
                                            : flexRender(header.column.columnDef.header, header.getContext())}
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
                                const rowPk = (row.original as Record<string, unknown>)?.[rowIdField] ?? row.id;
                                const isSelected = selectedRowId != null && String(rowPk) === selectedRowId;
                                return (
                                    <TableRow
                                        key={row.id}
                                        data-state={isSelected ? 'selected' : row.getIsSelected() ? 'selected' : undefined}
                                        className={onRowSelect ? 'cursor-pointer' : undefined}
                                        onClick={onRowSelect ? () => onRowSelect(isSelected ? null : String(rowPk)) : undefined}
                                    >
                                        {row.getVisibleCells().map((cell) => (
                                            <TableCell key={cell.id}>
                                                {flexRender(cell.column.columnDef.cell, cell.getContext())}
                                            </TableCell>
                                        ))}
                                    </TableRow>
                                );
                            })
                        )}
                    </TableBody>
                </Table>
            </div>
            <div className="flex items-center justify-between px-3 py-2 border-t border-border mt-auto shrink-0">
                <div className="flex items-center gap-2 text-sm text-muted-foreground">
                    <span>Rows per page</span>
                    <Select
                        value={String(pageSize)}
                        onValueChange={(val) => onPageSizeChange(Number(val))}
                    >
                        <SelectTrigger size="sm" className="w-auto h-8" aria-label="Rows per page">
                            <SelectValue />
                        </SelectTrigger>
                        <SelectContent>
                            {PAGE_SIZE_OPTIONS.map((size) => (
                                <SelectItem key={size} value={String(size)}>
                                    {size}
                                </SelectItem>
                            ))}
                        </SelectContent>
                    </Select>
                </div>
                <div className="flex items-center gap-1.5">
                    <span className="text-sm text-muted-foreground mr-2">
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
