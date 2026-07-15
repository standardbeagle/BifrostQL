import { useState, useCallback, useEffect, useMemo, useRef } from 'react';
import { useDataTable } from './hooks/useDataTable';
import { useDeleteMutation } from './hooks/useDeleteMutation';
import { useTableMutation } from './hooks/useTableMutation';
import { useToast } from './hooks/useToast';
import { DataEditDialog } from './data-edit';
import { DataTable } from './components/data-table';
import { ConfirmDialog } from './components/confirm-dialog';
import { ContentPanel, type ContentPanelTarget } from './components/content-panel';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { isJsonColumn } from './lib/content-detect';
import { Table, Column } from './types/schema';
import { ColumnPanel } from './data-panel';
import { encodePkRoute, pkFilterFor, buildPkEqFilter, type PkFilter } from './lib/row-id';
import { buildSingleRowQuery } from './lib/query-builder';
import { useFetcher } from './common/fetcher';

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

/**
 * Content-panel target. The row is snapshotted by PRIMARY KEY at open time,
 * not by grid index: a background refetch (mutation invalidation, window-focus
 * refetch) can reorder or remove rows, and an index captured at open time would
 * silently re-point the panel — display AND save — at whatever row now sits at
 * that position.
 */
interface PanelState {
    columnName: string;
    /** PK snapshot of the opened row; null when the table has no usable PK. */
    pk: PkFilter | null;
    /** Grid index at open time — prev/next seed and PK-less-table fallback. */
    rowIndex: number;
    /**
     * Table the panel was opened on. The component instance is reused across
     * table switches, so without this a stale panel would match the NEW
     * table's rows by index (PK-less fallback) or fire the row-gone error
     * toast on routine navigation.
     */
    tableName: string;
}

export function DataDataTable({ table, id, tableFilter, filterColumn, selectedRowId, onRowSelect, onOpenColumn, stackingEnabled, onToggleStacking }: DataDataTableParams): JSX.Element {
    const deleteMutation = useDeleteMutation(table);

    const [deleteTarget, setDeleteTarget] = useState<DeleteTarget | null>(null);
    // Encoded PK route of the row being edited, or null when the dialog is closed.
    // Editing in place (local state) instead of routing keeps the surrounding drill
    // context — parent → child → grandchild, side columns — mounted while the dialog
    // is open, so saved changes refetch in place rather than yanking the view to the
    // edited table's root.
    const [editTarget, setEditTarget] = useState<string | null>(null);

    // Content panel state — see PanelState for why the row is keyed by PK.
    const [panel, setPanel] = useState<PanelState | null>(null);

    // Rows mirror for openPanelAt. A ref (not a dep) keeps the expand callback
    // referentially stable so the grid columns don't rebuild on every fetch.
    const rowsRef = useRef<readonly Record<string, unknown>[]>([]);

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
        const row = rowsRef.current[rowIndex];
        setPanel({ columnName, pk: row ? pkFilterFor(row, table) : null, rowIndex, tableName: table.name });
    }, [table]);

    const {
        columns,
        sorting,
        columnFilters,
        pageIndex,
        pageSize,
        pageCount,
        rows,
        totalRows,
        exportRows,
        loading,
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

    // Keep the expand callback's row mirror current (see rowsRef above).
    rowsRef.current = rows as readonly Record<string, unknown>[];

    const { toast } = useToast();
    const fetcher = useFetcher();
    const isEditable = table.isEditable !== false;

    const panelPkRoute = useMemo(
        () => (panel?.pk ? encodePkRoute(panel.pk, table) : null),
        [panel, table],
    );
    // Locate the panel's row by its snapshotted PK — never by stored grid index,
    // which a background refetch can silently re-point at a different row. The
    // open-time index is only trusted for PK-less tables (view-only there).
    const panelRowIndex = useMemo(() => {
        if (!panel) return -1;
        // Stale panel from a previous table (cleanup effect hasn't run yet):
        // never resolve it against the new table's rows.
        if (panel.tableName !== table.name) return -1;
        if (panelPkRoute === null) return panel.rowIndex < rows.length ? panel.rowIndex : -1;
        return rows.findIndex((r) => {
            const pk = pkFilterFor(r as Record<string, unknown>, table);
            return pk !== null && encodePkRoute(pk, table) === panelPkRoute;
        });
    }, [panel, panelPkRoute, rows, table]);
    const panelRow = panelRowIndex >= 0 ? (rows[panelRowIndex] as Record<string, unknown>) : undefined;

    // Table switched under a reused component instance: drop the stale panel
    // silently — it belongs to the previous table, so neither the row-gone
    // error toast nor the PK-less index fallback should see the new rows.
    useEffect(() => {
        if (panel !== null && panel.tableName !== table.name) {
            setPanel(null);
        }
    }, [panel, table.name]);

    // Row gone: the snapshotted row left the current page (deleted, or filtered/
    // paged away by a refetch). Close the panel rather than showing — or worse,
    // saving — some other row's data.
    useEffect(() => {
        if (panel !== null && panel.tableName === table.name && !loading && panelRowIndex === -1) {
            setPanel(null);
            toast('Row is no longer in the current view; panel closed.', 'error');
        }
    }, [panel, table.name, loading, panelRowIndex, toast]);

    // Memoized: JSON columns re-stringify the raw value, which for multi-MB
    // payloads would otherwise run on every grid render while the panel is open.
    const panelTarget: ContentPanelTarget | null = useMemo(() => {
        if (!panel || !panelRow) return null;
        const col = table.columns.find((c: Column) => c.name === panel.columnName);
        if (!col) return null;
        const rawValue = panelRow[panel.columnName];
        // Native JSON columns come back parsed (object/array/number/string) — always
        // serialize so the panel edits canonical JSON text (a stored JSON string
        // "123" shows as "123", round-tripping losslessly) rather than "[object
        // Object]" or a bare unquoted primitive.
        const isJsonCol = isJsonColumn(col);
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
            rowKey: panelPkRoute ?? `row-${panel.rowIndex}`,
            isReadOnly: col.isReadOnly || col.isPrimaryKey || col.isIdentity,
        };
    }, [panel, panelRow, panelPkRoute, table]);

    // Single-field save from the content expand panel. Reuses the row update
    // mutation, keyed by the PK snapshotted when the panel opened — immune to
    // grid reorder. Update_ marks only non-nullable columns required and SETs
    // only provided fields, so the fresh read + echo carries just the
    // non-nullable editable columns plus the edited one; untouched nullable
    // (often wide) columns skip the round trip entirely.
    const panelPkEq = useMemo(() => (panel?.pk ? buildPkEqFilter(panel.pk, table) : null), [panel, table]);
    const editableColumns = useMemo(
        () => table.columns.filter((c: Column) => !c.isReadOnly && !c.isIdentity),
        [table],
    );
    const panelWriteColumns = useMemo(
        () => editableColumns.filter((c: Column) => !c.isNullable || c.name === panel?.columnName),
        [editableColumns, panel?.columnName],
    );
    const contentEditColumns = useMemo(() => panelWriteColumns.map((column: Column) => ({ column })), [panelWriteColumns]);
    const contentIdColumns = useMemo(() => {
        const byName = new Map(table.columns.map((c: Column) => [c.name, c] as const));
        return (table.primaryKeys ?? []).map((pk) => byName.get(pk)).filter((c): c is Column => !!c);
    }, [table]);
    const contentMutation = useTableMutation(table, contentEditColumns, contentIdColumns, panelPkRoute ?? '');

    // Save is only possible when the table is editable and the row has a real PK
    // (every key column present); otherwise ContentPanel hides Edit (no silent drop).
    // Also block while a save is pending. Grid staleness no longer gates saving —
    // the write re-reads the row fresh below — but a concurrent save still must not
    // overlap.
    const canSaveContent = isEditable && panelPkEq !== null && !contentMutation.isPending;
    const handlePanelSave = useCallback(async (value: string) => {
        if (!panel || !panelPkEq) return;
        const col = table.columns.find((c: Column) => c.name === panel.columnName);
        // Native JSON columns take a JSON value, not a JSON string — parse the
        // edited text back so it isn't stored double-encoded. Non-JSON (longtext,
        // xml, etc.) and unparseable input pass through as the raw string.
        let payload: unknown = value;
        if (col && isJsonColumn(col)) {
            try { payload = JSON.parse(value); } catch { /* send raw; server validates */ }
        }
        try {
            // Re-read the row fresh (by the snapshotted PK) so echoed columns
            // reflect current server state, not the possibly-stale grid snapshot.
            // Failing to fetch aborts the save (editor stays open) rather than
            // writing a stale row.
            const freshQuery = buildSingleRowQuery(table, panelPkEq, panelWriteColumns.map((c: Column) => c.name));
            const res = await fetcher.query<{ value: { data: Record<string, unknown>[] } }>(freshQuery, panelPkEq.variables);
            const fresh = res?.value?.data?.[0];
            if (!fresh) throw new Error('Row no longer exists.');

            const detail: Record<string, unknown> = {};
            for (const c of panelWriteColumns) detail[c.name] = fresh[c.name] ?? null;
            detail[panel.columnName] = payload;
            await contentMutation.update(detail);
        } catch (e: unknown) {
            toast(`Save failed: ${(e as Error).message}`, 'error');
            // Rethrow so ContentPanel keeps the editor open on failure.
            throw e;
        }
    }, [panel, panelPkEq, table, panelWriteColumns, contentMutation, fetcher, toast]);

    const handlePanelClose = useCallback(() => {
        setPanel(null);
    }, []);

    const handlePanelNavigate = useCallback((direction: 'prev' | 'next') => {
        if (!panel || panelRowIndex < 0) return;
        const next = direction === 'prev'
            ? Math.max(0, panelRowIndex - 1)
            : Math.min(rows.length - 1, panelRowIndex + 1);
        handleExpandContent(next, panel.columnName);
    }, [panel, panelRowIndex, rows.length, handleExpandContent]);

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
                exportRows={exportRows}
                totalRows={totalRows}
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
                canNavigateNext={panelRowIndex >= 0 && panelRowIndex < rows.length - 1}
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
