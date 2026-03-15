import { ColumnDef, flexRender, getCoreRowModel, useReactTable } from '@tanstack/react-table';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Link } from './hooks/usePath';
import { useSchema } from './hooks/useSchema';
import { Table as SchemaTable } from './types/schema';
import { Loader2 } from 'lucide-react';
import { Alert, AlertDescription } from '@/components/ui/alert';

const columns: ColumnDef<SchemaTable, unknown>[] = [
    {
        id: 'table',
        header: 'Table',
        cell: ({ row }) => (
            <Link to={`/${row.original.name}`} className="block no-underline text-foreground hover:text-primary py-1 px-1 text-sm font-medium">
                {row.original.label}
            </Link>
        ),
    }
];

export function TableList() {
    const {loading, error, data} = useSchema();

    const table = useReactTable({
        data,
        columns,
        getCoreRowModel: getCoreRowModel(),
    });

    if (loading) return (
        <div className="flex items-center justify-center gap-2 p-4">
            <Loader2 className="size-4 animate-spin text-muted-foreground" />
            <span className="text-sm text-muted-foreground">Loading...</span>
        </div>
    );
    if (error) return (
        <Alert variant="destructive" className="m-2">
            <AlertDescription>Error: {error.message}</AlertDescription>
        </Alert>
    );
    if (data.length === 0) return (
        <div className="p-4 text-center text-sm text-muted-foreground">
            <p className="font-medium">No tables found</p>
            <p className="mt-1">The database may be empty or the connection may have failed.</p>
        </div>
    );

    return (
        <div>
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
                    {table.getRowModel().rows.map((row) => (
                        <TableRow key={row.id}>
                            {row.getVisibleCells().map((cell) => (
                                <TableCell key={cell.id}>
                                    {flexRender(cell.column.columnDef.cell, cell.getContext())}
                                </TableCell>
                            ))}
                        </TableRow>
                    ))}
                </TableBody>
            </Table>
        </div>
    );
}
