import { useState } from 'react';
import {
    ColumnDef,
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
    DropdownMenu,
    DropdownMenuCheckboxItem,
    DropdownMenuContent,
    DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu';
import { ChevronDown } from 'lucide-react';

interface DataTableProps<TData> {
    columns: ColumnDef<TData, unknown>[];
    data: TData[];
    pageCount: number;
    pageIndex: number;
    pageSize: number;
    sorting: SortingState;
    loading?: boolean;
    onSortingChange: (sorting: SortingState) => void;
    onPageIndexChange: (pageIndex: number) => void;
    onPageSizeChange: (pageSize: number) => void;
}

const PAGE_SIZE_OPTIONS = [10, 20, 30, 50, 100];

export function DataTable<TData>({
    columns,
    data,
    pageCount,
    pageIndex,
    pageSize,
    sorting,
    loading,
    onSortingChange,
    onPageIndexChange,
    onPageSizeChange,
}: DataTableProps<TData>) {
    const [columnVisibility, setColumnVisibility] = useState<VisibilityState>({});

    const table = useReactTable({
        data,
        columns,
        pageCount,
        state: {
            sorting,
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
        onColumnVisibilityChange: setColumnVisibility,
        getCoreRowModel: getCoreRowModel(),
        manualSorting: true,
        manualPagination: true,
    });

    return (
        <div className="editdb-data-table-wrapper">
            <div className="flex items-center justify-end py-2 px-1">
                <DropdownMenu>
                    <DropdownMenuTrigger asChild>
                        <Button variant="outline" size="sm" className="ml-auto">
                            Columns <ChevronDown className="ml-2 size-4" />
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
            <Table>
                <TableHeader>
                    {table.getHeaderGroups().map((headerGroup) => (
                        <TableRow key={headerGroup.id}>
                            {headerGroup.headers.map((header) => (
                                <TableHead
                                    key={header.id}
                                    className={header.column.getCanSort() ? 'cursor-pointer select-none' : ''}
                                    onClick={header.column.getToggleSortingHandler()}
                                >
                                    {header.isPlaceholder
                                        ? null
                                        : flexRender(header.column.columnDef.header, header.getContext())}
                                    {header.column.getIsSorted() === 'asc' ? ' \u25B2' : ''}
                                    {header.column.getIsSorted() === 'desc' ? ' \u25BC' : ''}
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
                        table.getRowModel().rows.map((row) => (
                            <TableRow key={row.id}>
                                {row.getVisibleCells().map((cell) => (
                                    <TableCell key={cell.id}>
                                        {flexRender(cell.column.columnDef.cell, cell.getContext())}
                                    </TableCell>
                                ))}
                            </TableRow>
                        ))
                    )}
                </TableBody>
            </Table>
            <div className="flex items-center justify-between px-2 py-4">
                <div className="flex items-center gap-2 text-sm text-muted-foreground">
                    <span>Rows per page</span>
                    <select
                        className="h-8 rounded-md border border-input bg-background px-2 text-sm"
                        value={pageSize}
                        onChange={(e) => onPageSizeChange(Number(e.target.value))}
                    >
                        {PAGE_SIZE_OPTIONS.map((size) => (
                            <option key={size} value={size}>{size}</option>
                        ))}
                    </select>
                </div>
                <div className="flex items-center gap-2">
                    <span className="text-sm text-muted-foreground">
                        Page {pageIndex + 1} of {pageCount || 1}
                    </span>
                    <Button
                        variant="outline"
                        size="sm"
                        onClick={() => onPageIndexChange(0)}
                        disabled={pageIndex === 0}
                    >
                        {'<<'}
                    </Button>
                    <Button
                        variant="outline"
                        size="sm"
                        onClick={() => onPageIndexChange(pageIndex - 1)}
                        disabled={!table.getCanPreviousPage()}
                    >
                        {'<'}
                    </Button>
                    <Button
                        variant="outline"
                        size="sm"
                        onClick={() => onPageIndexChange(pageIndex + 1)}
                        disabled={!table.getCanNextPage()}
                    >
                        {'>'}
                    </Button>
                    <Button
                        variant="outline"
                        size="sm"
                        onClick={() => onPageIndexChange(pageCount - 1)}
                        disabled={pageIndex >= pageCount - 1}
                    >
                        {'>>'}
                    </Button>
                </div>
            </div>
        </div>
    );
}
