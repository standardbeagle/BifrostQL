import { useState, useCallback, useMemo } from 'react';
import { useDataTable } from './hooks/useDataTable';
import { useDeleteMutation } from './hooks/useDeleteMutation';
import { useTableMutation } from './hooks/useTableMutation';
import { useToast } from './hooks/useToast';
import { DataEditDialog } from './data-edit';
import { DataTable } from './components/data-table';
import { ConfirmDialog } from './components/confirm-dialog';
import { ContentPanel, type ContentPanelTarget } from './components/content-panel';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { isJsonDbType } from './lib/content-detect';
import { Table, Column } from './types/schema';
import { ColumnPanel } from './data-panel';
import { encodePkRoute, pkFilterFor, type PkFilter } from './lib/row-id';

interface DataDataTableParams {
    table: Table;
    id?: string;
    tableFilter?: string;
    filterColumn?: string;
    selectedRowId?: string | null;
    onRowSelect?: (rowId: string | null) => void;
    onOpenColumn?: (panel: ColumnPanel) => void;
    /** Current stacking (parent/child drill-down) mode state. */
    stackingEnabled?: boolean;
    /** Toggle stacking mode. When supplied, the grid renders the mode toggle. */
    onToggleStacking?: (next: boolean) => void;
}

type DeleteTarget =
    | { type: 'single'; pk: PkFilter }
    | { type: 'batch'; pks: PkFilter[] };

export function DataDataTable({ table, id, tableFilter, filterColumn, selectedRowId, onRowSelect, onOpenColumn, stackingEnabled, onToggleStacking }: DataDataTableParams): JSX.Element {
    const deleteMutation = useDeleteMutation(table);

    const [deleteTarget, setDeleteTarget] = useState<DeleteTarget | null>(null);
    // Encoded PK route of the row being edited, or null when the dialog is closed.
    // Editing in place (local state) instead of routing keeps the surrounding drill
    // context — parent → child → grandchild, side columns — mounted while the dialog
    // is open, so saved changes refetch in place rather than yanking the view to the
    // edited table's root.
    const [editTarget, setEditTarget] = useState<string | null>(null);

    // Content panel state
    const [panelColumn, setPanelColumn] = useState<string | null>(null);
    const [panelRowIndex, setPanelRowIndex] = useState<number>(0);

    const handleEditRow = useCallback((pk: PkFilter) => {
        setEditTarget(encodePkRoute(pk, table));
    }, [table]);

    const handleDeleteRow = useCallback((pk: PkFilter) => {
        setDeleteTarget({ type: 'single', pk });
    }, []);

    const handleDeleteSelected = useCallback((pks: PkFilter[]) => {
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
        pageIndex,
        pageSize,
        pageCount,
        rows,
        loading,
        fetching,
        error,
        onSortingChange,
        onColumnFiltersChange,
        onPageIndexChange,
        onPageSizeChange,
    } = useDataTable(table, id, tableFilter, filterColumn, handleExpandContent, onOpenColumn);

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
        const rawValue = row[panelColumn];
        // Native JSON columns come back parsed (object/array/number/string) — always
        // serialize so the panel edits canonical JSON text (a stored JSON string
        // "123" shows as "123", round-tripping losslessly) rather than "[object
        // Object]" or a bare unquoted primitive.
        const isJsonCol = isJsonDbType(col.dbType) || col.paramType === 'JSON';
        const stringValue = rawValue === null || rawValue === undefined
            ? ''
            : isJsonCol
                ? JSON.stringify(rawValue, null, 2)
                : typeof rawValue === 'object'
                    ? JSON.stringify(rawValue, null, 2)
                    : String(rawValue);
        return {
            value: stringValue,
            columnName: col.name,
            columnLabel: col.label,
            dbType: col.dbType,
            rowIndex: panelRowIndex,
            isReadOnly: col.isReadOnly || col.isPrimaryKey || col.isIdentity,
        };
    })();

    // Single-field save from the content expand panel. Reuses the row update
    // mutation. BifrostQL's Update_ input marks every non-nullable column
    // required, so the whole editable row is sent (from the row already loaded in
    // the grid) with just the edited column overridden.
    const { toast } = useToast();
    const isEditable = table.isEditable !== false;
    const panelRow = rows[panelRowIndex] as Record<string, unknown> | undefined;
    const panelPk = useMemo(() => (panelRow ? pkFilterFor(panelRow, table) : null), [panelRow, table]);
    const panelPkRoute = useMemo(() => (panelPk ? encodePkRoute(panelPk, table) : ''), [panelPk, table]);
    const editableColumns = useMemo(
        () => table.columns.filter((c: Column) => !c.isReadOnly && !c.isIdentity),
        [table],
    );
    const contentEditColumns = useMemo(() => editableColumns.map((column: Column) => ({ column })), [editableColumns]);
    const contentIdColumns = useMemo(() => {
        const byName = new Map(table.columns.map((c: Column) => [c.name, c] as const));
        return (table.primaryKeys ?? []).map((pk) => byName.get(pk)).filter((c): c is Column => !!c);
    }, [table]);
    const contentMutation = useTableMutation(table, contentEditColumns, contentIdColumns, panelPkRoute);

    // Save is only possible when the table is editable and the row has a real PK
    // (every key column present); otherwise ContentPanel hides Edit (no silent drop).
    // Also block while a fetch/refetch is in flight or a save is pending — the
    // full-row write is built from the visible row snapshot, so saving against
    // stale/placeholder rows could revert a just-saved (or concurrent) change.
    const canSaveContent = isEditable && panelPk !== null && !fetching && !contentMutation.isPending;
    const handlePanelSave = useCallback((value: string) => {
        if (!panelColumn || !panelRow) return;
        const col = table.columns.find((c: Column) => c.name === panelColumn);
        // Native JSON columns take a JSON value, not a JSON string — parse the
        // edited text back so it isn't stored double-encoded. Non-JSON (longtext,
        // xml, etc.) and unparseable input pass through as the raw string.
        let payload: unknown = value;
        if (col && (isJsonDbType(col.dbType) || col.paramType === 'JSON')) {
            try { payload = JSON.parse(value); } catch { /* send raw; server validates */ }
        }
        const detail: Record<string, unknown> = {};
        for (const c of editableColumns) detail[c.name] = panelRow[c.name] ?? null;
        detail[panelColumn] = payload;
        return contentMutation.update(detail).then(() => {}).catch((e: unknown) => {
            toast(`Save failed: ${(e as Error).message}`, 'error');
            // Rethrow so ContentPanel keeps the editor open on failure.
            throw e;
        });
    }, [panelColumn, panelRow, table, editableColumns, contentMutation, toast]);

    const handlePanelClose = useCallback(() => {
        setPanelColumn(null);
    }, []);

    const handlePanelNavigate = useCallback((direction: 'prev' | 'next') => {
        setPanelRowIndex((idx) => {
            if (direction === 'prev') return Math.max(0, idx - 1);
            return Math.min(rows.length - 1, idx + 1);
        });
    }, [rows.length]);

    if (error) return (
        <Alert variant="destructive" className="m-2">
            <AlertDescription>Error: {error.message}</AlertDescription>
        </Alert>
    );

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
                primaryKeys={table.primaryKeys ?? []}
                selectable={isEditable}
                selectedRowId={selectedRowId}
                onRowSelect={onRowSelect}
                onSortingChange={onSortingChange}
                onColumnFiltersChange={onColumnFiltersChange}
                onPageIndexChange={onPageIndexChange}
                onPageSizeChange={onPageSizeChange}
                onEditRow={isEditable ? handleEditRow : undefined}
                onDeleteRow={isEditable ? handleDeleteRow : undefined}
                onDeleteSelected={isEditable ? handleDeleteSelected : undefined}
                stackingEnabled={stackingEnabled}
                onToggleStacking={onToggleStacking}
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
                onSave={canSaveContent ? handlePanelSave : undefined}
                canNavigatePrev={panelRowIndex > 0}
                canNavigateNext={panelRowIndex < rows.length - 1}
            />
            {editTarget !== null && (
                <DataEditDialog
                    table={table.graphQlName}
                    editid={editTarget}
                    onClose={() => setEditTarget(null)}
                />
            )}
        </>
    );
}
