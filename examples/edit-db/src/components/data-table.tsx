import { useState, useEffect } from 'react';
import {
    ColumnDef,
    ColumnFiltersState,
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
    rowIdField?: string;
    selectedRowId?: string | null;
    onRowSelect?: (rowId: string | null) => void;
    onSortingChange: (sorting: SortingState) => void;
    onColumnFiltersChange: (filters: ColumnFiltersState) => void;
    onPageIndexChange: (pageIndex: number) => void;
    onPageSizeChange: (pageSize: number) => void;
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
    rowIdField = 'id',
    selectedRowId,
    onRowSelect,
    onSortingChange,
    onColumnFiltersChange,
    onPageIndexChange,
    onPageSizeChange,
}: DataTableProps<TData>) {
    const [columnVisibility, setColumnVisibility] = useState<VisibilityState>({});
    const [initialized, setInitialized] = useState(false);

    useEffect(() => {
        if (!initialized) {
            onPageSizeChange(getScreenPageSize());
            setInitialized(true);
        }
    }, [initialized, onPageSizeChange]);

    const table = useReactTable({
        data,
        columns,
        pageCount,
        state: {
            sorting,
            columnFilters,
            pagination: { pageIndex, pageSize },
            columnVisibility,
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
        getCoreRowModel: getCoreRowModel(),
        manualSorting: true,
        manualFiltering: true,
        manualPagination: true,
    });

    return (
        <div className="flex flex-col w-full min-h-0 flex-1">
            <div className="flex items-center justify-end py-2 px-1">
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
            <div className="flex-1">
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
                                <TableCell colSpan={columns.length} className="h-24 text-center">
                                    Loading...
                                </TableCell>
                            </TableRow>
                        ) : table.getRowModel().rows.length === 0 ? (
                            <TableRow>
                                <TableCell colSpan={columns.length} className="h-24 text-center">
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
                                        data-state={isSelected ? 'selected' : undefined}
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
            <div className="flex items-center justify-between px-4 py-3 border-t border-border mt-auto">
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
                    >
                        <ChevronsLeft className="size-4" />
                    </Button>
                    <Button
                        variant="outline"
                        size="icon-sm"
                        onClick={() => onPageIndexChange(pageIndex - 1)}
                        disabled={!table.getCanPreviousPage()}
                        aria-label="Previous page"
                    >
                        <ChevronLeft className="size-4" />
                    </Button>
                    <Button
                        variant="outline"
                        size="icon-sm"
                        onClick={() => onPageIndexChange(pageIndex + 1)}
                        disabled={!table.getCanNextPage()}
                        aria-label="Next page"
                    >
                        <ChevronRight className="size-4" />
                    </Button>
                    <Button
                        variant="outline"
                        size="icon-sm"
                        onClick={() => onPageIndexChange(pageCount - 1)}
                        disabled={pageIndex >= pageCount - 1}
                        aria-label="Last page"
                    >
                        <ChevronsRight className="size-4" />
                    </Button>
                </div>
            </div>
        </div>
    );
}
