import { useMemo, useState } from 'react';
import {
    Dialog,
    DialogContent,
    DialogDescription,
    DialogFooter,
    DialogHeader,
    DialogTitle,
} from '@/components/ui/dialog';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import type { Column, Table } from '../types/schema';
import type { PkFilter } from '../lib/row-id';
import { buildRowsByPkQuery } from '../lib/query-builder';
import { buildBulkUpdatePayloads } from '../lib/mutation-payload';
import { isBinaryDbType } from '../lib/content-detect';
import { useDeltaMutation } from '../hooks/useDeltaMutation';
import { useFetcher } from '../common/fetcher';
import { useToast } from '../hooks/useToast';

interface BulkEditDialogProps {
    table: Table;
    /** Primary-key filters of the selected rows (composite-safe, from pkFilterFor). */
    pks: PkFilter[];
    onClose: () => void;
}

interface FieldState {
    enabled: boolean;
    value: string;
    setNull: boolean;
}

/**
 * Jira-style bulk edit: apply one shared set of field changes to every selected
 * row, saved as ONE `delta: { updated }` document — one server transaction (a
 * failure anywhere applies nothing) that rides the set-based bulk fast path at
 * batch scale. Before saving, the selected rows are re-read fresh in a single
 * query so echoed non-nullable columns reflect current server state (the grid's
 * projection excludes large values and may be stale).
 */
export function BulkEditDialog({ table, pks, onClose }: BulkEditDialogProps) {
    const fetcher = useFetcher();
    const { toast } = useToast();
    const deltaMutation = useDeltaMutation(table);
    const [fields, setFields] = useState<Record<string, FieldState>>({});
    const [error, setError] = useState<string | null>(null);
    const [isFetching, setIsFetching] = useState(false);

    // Binary columns cannot round-trip through the fresh-read echo, so they are
    // not offered; a NOT NULL binary column makes the echo set unbuildable at all.
    const editableColumns = useMemo(
        () => table.columns.filter((c: Column) =>
            !c.isReadOnly && !c.isIdentity && !c.isPrimaryKey && !isBinaryDbType(c.dbType)),
        [table],
    );
    const blockedByBinary = useMemo(
        () => table.columns.some((c: Column) =>
            !c.isReadOnly && !c.isIdentity && !c.isPrimaryKey && !c.isNullable && isBinaryDbType(c.dbType)),
        [table],
    );
    const idColumns = useMemo(() => {
        const byName = new Map(table.columns.map((c: Column) => [c.name, c] as const));
        return (table.primaryKeys ?? []).map((pk) => byName.get(pk)).filter((c): c is Column => !!c);
    }, [table]);

    const enabledColumns = editableColumns.filter((c) => fields[c.name]?.enabled);
    const isPending = isFetching || deltaMutation.isPending;

    const setField = (name: string, patch: Partial<FieldState>) =>
        setFields((prev) => {
            const current = prev[name] ?? { enabled: false, value: '', setNull: false };
            return { ...prev, [name]: { ...current, ...patch } };
        });

    const changeValue = (column: Column): unknown => {
        const state = fields[column.name]!;
        if (state.setNull) return null;
        if (column.paramType.startsWith('Boolean')) return state.value === 'true';
        return state.value;
    };

    const handleSave = async () => {
        setError(null);
        if (enabledColumns.length === 0) {
            setError('Choose at least one field to change.');
            return;
        }
        setIsFetching(true);
        try {
            const changes: Record<string, unknown> = {};
            for (const column of enabledColumns) changes[column.name] = changeValue(column);

            // The write set: every non-nullable editable column (the Update_<t> input
            // requires them) plus the changed columns; keys ride along for identity.
            const writeColumns = editableColumns.filter(
                (c) => !c.isNullable || changes[c.name] !== undefined,
            );
            const fetchFields = [
                ...new Set([...(table.primaryKeys ?? []), ...writeColumns.map((c) => c.name)]),
            ];
            const rowsQuery = buildRowsByPkQuery(table, pks, fetchFields);
            if (!rowsQuery) throw new Error('The selected rows have no resolvable primary keys.');
            const res = await fetcher.query<{ value: { data: Record<string, unknown>[] } }>(
                rowsQuery.query, rowsQuery.variables);
            const freshRows = res?.value?.data ?? [];
            if (freshRows.length !== pks.length)
                throw new Error(`Only ${freshRows.length} of ${pks.length} selected rows still exist. Refresh and retry.`);

            const updated = buildBulkUpdatePayloads(table, writeColumns, idColumns, freshRows, changes);
            await deltaMutation.saveDelta({ updated });
            onClose();
        } catch (e: unknown) {
            const message = (e as Error).message ?? String(e);
            setError(message);
            toast(`Bulk edit failed: ${message}`, 'error');
        } finally {
            setIsFetching(false);
        }
    };

    return (
        <Dialog open onOpenChange={(open) => { if (!open && !isPending) onClose(); }}>
            <DialogContent showCloseButton={false} className="max-w-lg">
                <DialogHeader>
                    <DialogTitle>Edit {pks.length} {pks.length === 1 ? 'row' : 'rows'}</DialogTitle>
                    <DialogDescription>
                        Chosen fields are applied to every selected row and saved in one
                        transaction — if any row fails, nothing changes.
                    </DialogDescription>
                </DialogHeader>
                {blockedByBinary ? (
                    <p className="text-sm text-destructive">
                        This table has a required binary column, which bulk edit cannot echo. Edit rows individually.
                    </p>
                ) : (
                    <div className="flex flex-col gap-2 max-h-80 overflow-y-auto py-1">
                        {editableColumns.map((column) => {
                            const state = fields[column.name] ?? { enabled: false, value: '', setNull: false };
                            const isBoolean = column.paramType.startsWith('Boolean');
                            return (
                                <div key={column.name} className="flex items-center gap-2">
                                    <Checkbox
                                        id={`bulk-${column.name}`}
                                        checked={state.enabled}
                                        onCheckedChange={(checked) => setField(column.name, { enabled: checked === true })}
                                    />
                                    <Label htmlFor={`bulk-${column.name}`} className="w-40 truncate" title={column.label ?? column.name}>
                                        {column.label ?? column.name}
                                    </Label>
                                    {isBoolean ? (
                                        <select
                                            className="h-8 rounded-md border bg-transparent px-2 text-sm flex-1"
                                            disabled={!state.enabled || state.setNull}
                                            value={state.value === 'true' ? 'true' : 'false'}
                                            onChange={(e) => setField(column.name, { value: e.target.value })}
                                        >
                                            <option value="true">true</option>
                                            <option value="false">false</option>
                                        </select>
                                    ) : (
                                        <Input
                                            className="h-8 flex-1"
                                            disabled={!state.enabled || state.setNull}
                                            value={state.value}
                                            onChange={(e) => setField(column.name, { value: e.target.value })}
                                        />
                                    )}
                                    {column.isNullable && (
                                        <label className="flex items-center gap-1 text-xs text-muted-foreground">
                                            <Checkbox
                                                checked={state.setNull}
                                                disabled={!state.enabled}
                                                onCheckedChange={(checked) => setField(column.name, { setNull: checked === true })}
                                            />
                                            null
                                        </label>
                                    )}
                                </div>
                            );
                        })}
                    </div>
                )}
                {error && <p className="text-sm text-destructive">{error}</p>}
                <DialogFooter>
                    <Button variant="outline" onClick={onClose} disabled={isPending}>
                        Cancel
                    </Button>
                    <Button onClick={handleSave} disabled={isPending || blockedByBinary}>
                        {isPending ? 'Saving…' : `Apply to ${pks.length} ${pks.length === 1 ? 'row' : 'rows'}`}
                    </Button>
                </DialogFooter>
            </DialogContent>
        </Dialog>
    );
}
