import { useState, useCallback } from 'react';
import { useDataTable } from './hooks/useDataTable';
import { useDeleteMutation } from './hooks/useDeleteMutation';
import { DataTable } from './components/data-table';
import { ConfirmDialog } from './components/confirm-dialog';
import { Table } from './types/schema';

interface DataDataTableParams {
    table: Table;
    id?: string;
    tableFilter?: string;
    filterColumn?: string;
    selectedRowId?: string | null;
    onRowSelect?: (rowId: string | null) => void;
}

export function DataDataTable({ table, id, tableFilter, filterColumn, selectedRowId, onRowSelect }: DataDataTableParams): JSX.Element {
    const deleteMutation = useDeleteMutation(table);

    // Delete confirmation state
    const [deleteTarget, setDeleteTarget] = useState<{ type: 'single'; pk: string } | { type: 'batch'; pks: string[] } | null>(null);

    const handleDeleteRow = useCallback((pk: string) => {
        setDeleteTarget({ type: 'single', pk });
    }, []);

    const handleDeleteSelected = useCallback((pks: string[]) => {
        if (pks.length === 0) return;
        setDeleteTarget({ type: 'batch', pks });
    }, []);

    const {
        columns,
        sorting,
        columnFilters,
        rowIdField,
        pageIndex,
        pageSize,
        pageCount,
        rows,
        loading,
        error,
        onSortingChange,
        onColumnFiltersChange,
        onPageIndexChange,
        onPageSizeChange,
    } = useDataTable(table, id, tableFilter, filterColumn, table.isEditable !== false ? handleDeleteRow : undefined);

    const handleConfirmDelete = useCallback(async () => {
        if (!deleteTarget) return;
        try {
            if (deleteTarget.type === 'single') {
                await deleteMutation.deleteRow(deleteTarget.pk);
            } else {
                await deleteMutation.deleteRows(deleteTarget.pks);
            }
            setDeleteTarget(null);
        } catch {
            // error is surfaced via deleteMutation.error
        }
    }, [deleteTarget, deleteMutation]);

    const deleteCount = deleteTarget?.type === 'batch' ? deleteTarget.pks.length : 1;

    if (error) return <div>Error: {error.message}</div>;

    return (
        <>
            <DataTable
                columns={columns}
                data={rows}
                pageCount={pageCount}
                pageIndex={pageIndex}
                pageSize={pageSize}
                sorting={sorting}
                columnFilters={columnFilters}
                loading={loading}
                rowIdField={rowIdField}
                selectable={table.isEditable !== false}
                selectedRowId={selectedRowId}
                onRowSelect={onRowSelect}
                onSortingChange={onSortingChange}
                onColumnFiltersChange={onColumnFiltersChange}
                onPageIndexChange={onPageIndexChange}
                onPageSizeChange={onPageSizeChange}
                onDeleteSelected={table.isEditable !== false ? handleDeleteSelected : undefined}
            />
            <ConfirmDialog
                open={deleteTarget !== null}
                onOpenChange={(open) => { if (!open) setDeleteTarget(null); }}
                title={`Delete ${deleteCount} ${deleteCount === 1 ? 'row' : 'rows'}?`}
                description={
                    <>
                        <p>This action cannot be undone.</p>
                        {deleteMutation.error && (
                            <p className="mt-2 text-sm text-destructive">{deleteMutation.error.message}</p>
                        )}
                    </>
                }
                confirmLabel={`Delete ${deleteCount === 1 ? 'row' : `${deleteCount} rows`}`}
                variant="destructive"
                isPending={deleteMutation.isPending}
                onConfirm={handleConfirmDelete}
            />
        </>
    );
}
