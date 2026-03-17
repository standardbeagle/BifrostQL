import { useState, useCallback } from 'react';
import { useDataTable } from './hooks/useDataTable';
import { useDeleteMutation } from './hooks/useDeleteMutation';
import { DataTable } from './components/data-table';
import { ConfirmDialog } from './components/confirm-dialog';
import { ContentPanel, type ContentPanelTarget } from './components/content-panel';
import { Table, Column } from './types/schema';
import { ColumnPanel } from './data-panel';

interface DataDataTableParams {
    table: Table;
    id?: string;
    tableFilter?: string;
    filterColumn?: string;
    selectedRowId?: string | null;
    onRowSelect?: (rowId: string | null) => void;
    onOpenColumn?: (panel: ColumnPanel) => void;
}

export function DataDataTable({ table, id, tableFilter, filterColumn, selectedRowId, onRowSelect, onOpenColumn }: DataDataTableParams): JSX.Element {
    const deleteMutation = useDeleteMutation(table);

    // Delete confirmation state
    const [deleteTarget, setDeleteTarget] = useState<{ type: 'single'; pk: string } | { type: 'batch'; pks: string[] } | null>(null);

    // Content panel state
    const [panelColumn, setPanelColumn] = useState<string | null>(null);
    const [panelRowIndex, setPanelRowIndex] = useState<number>(0);

    const handleDeleteRow = useCallback((pk: string) => {
        setDeleteTarget({ type: 'single', pk });
    }, []);

    const handleDeleteSelected = useCallback((pks: string[]) => {
        if (pks.length === 0) return;
        setDeleteTarget({ type: 'batch', pks });
    }, []);

    const handleExpandContent = useCallback((rowIndex: number, columnName: string) => {
        setPanelRowIndex(rowIndex);
        setPanelColumn(columnName);
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
    } = useDataTable(table, id, tableFilter, filterColumn, table.isEditable !== false ? handleDeleteRow : undefined, handleExpandContent, onOpenColumn);

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

    // Build content panel target from current state
    const panelTarget: ContentPanelTarget | null = (() => {
        if (panelColumn === null || !rows[panelRowIndex]) return null;
        const row = rows[panelRowIndex] as Record<string, unknown>;
        const col = table.columns.find((c: Column) => c.name === panelColumn);
        if (!col) return null;
        return {
            value: String(row[panelColumn] ?? ''),
            columnName: col.name,
            columnLabel: col.label,
            dbType: col.dbType,
            rowIndex: panelRowIndex,
            isReadOnly: col.isReadOnly || col.isPrimaryKey || col.isIdentity,
        };
    })();

    const handlePanelClose = useCallback(() => {
        setPanelColumn(null);
    }, []);

    const handlePanelNavigate = useCallback((direction: 'prev' | 'next') => {
        setPanelRowIndex((idx) => {
            if (direction === 'prev') return Math.max(0, idx - 1);
            return Math.min(rows.length - 1, idx + 1);
        });
    }, [rows.length]);

    if (error) return <div>Error: {error.message}</div>;

    return (
        <>
            <DataTable
                columns={columns}
                data={rows}
                tableName={table.name}
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
            <ContentPanel
                target={panelTarget}
                onClose={handlePanelClose}
                onNavigate={handlePanelNavigate}
                canNavigatePrev={panelRowIndex > 0}
                canNavigateNext={panelRowIndex < rows.length - 1}
            />
        </>
    );
}
