import { useState, useCallback, useEffect } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Link2, Plus, Unlink, Search, Loader2 } from 'lucide-react';
import type { ManyToManyJoin, Table } from '../types/schema';
import type { ColumnPanel } from '../data-panel';
import { useSchema } from '../hooks/useSchema';
import { useFetcher } from '../common/fetcher';
import { useTableMutation } from '../hooks/useTableMutation';
import { useDeleteMutation } from '../hooks/useDeleteMutation';
import { useToast } from '../hooks/useToast';
import { m2mRowsQuery, payloadColumns, targetDisplay, attachJunctionDetail, m2mTargetPickerPlan } from '../lib/m2m';
import { pkFilterFor } from '../lib/row-id';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { ConfirmDialog } from '@/components/confirm-dialog';
import { Link } from '../hooks/usePath';
import { formatColumnValue } from '../lib/format-value';

const M2M_ROW_LIMIT = 200;
// Server-side search narrows on the server, so a small window suffices; the
// client-filter fallback (non-String label) needs a larger window to stay usable.
const M2M_PICKER_SERVER_LIMIT = 50;
const M2M_PICKER_CLIENT_LIMIT = 500;

interface M2mPanelProps {
    parentTable: Table;
    m2m: ManyToManyJoin;
    parentRowId: string;
    onOpenColumn?: (panel: ColumnPanel) => void;
}

type JunctionRow = Record<string, unknown>;

/**
 * One detail tab for a many-to-many relationship. The junction is skipped for
 * navigation: each row shows the linked target entity (resolved through the
 * junction) alongside the junction payload columns. Links can be attached
 * (insert a junction row) and detached (delete the junction row by its primary
 * key).
 */
export function M2mPanel({ parentTable, m2m, parentRowId, onOpenColumn }: M2mPanelProps) {
    const schema = useSchema();
    const fetcher = useFetcher();
    const queryClient = useQueryClient();
    const { toast } = useToast();
    const [picking, setPicking] = useState(false);
    const [detachRow, setDetachRow] = useState<JunctionRow | null>(null);

    const junction = schema.findTable(m2m.junctionTable);
    const target = schema.findTable(m2m.targetTable);

    const detach = useDeleteMutation(junction ?? parentTable);

    const { query, variables } = junction && target
        ? m2mRowsQuery(junction, target, m2m, parentRowId)
        : { query: null as string | null, variables: {} };

    const queryKey = ['m2mRows', m2m.junctionTable, m2m.targetTable, parentRowId];
    const { data, isLoading, error } = useQuery({
        queryKey,
        queryFn: () => fetcher.query<Record<string, { data: JunctionRow[] }>>(query!, { ...variables, limit: M2M_ROW_LIMIT, offset: 0 }),
        enabled: !!query,
    });

    const invalidate = useCallback(() => queryClient.invalidateQueries({ queryKey }), [queryClient, queryKey]);

    const handleDetach = useCallback(async (junctionRow: JunctionRow) => {
        if (!junction) return;
        const pk = pkFilterFor(junctionRow, junction);
        if (!pk) return;
        await detach.deleteRow(pk);
        await invalidate();
    }, [junction, detach, invalidate]);

    if (!junction || !target) {
        return <div className="p-4 text-sm text-muted-foreground">Relationship schema unavailable.</div>;
    }

    const rows = data?.[junction.name]?.data ?? [];
    const payload = payloadColumns(junction, m2m);
    const isEditable = junction.isEditable !== false;

    return (
        <div className="flex flex-col min-h-0 flex-1 overflow-hidden">
            <div className="flex items-center gap-2 px-2 py-1 bg-muted/20 border-b border-border shrink-0">
                <span className="inline-flex items-center gap-1 rounded-full border border-border bg-muted/40 px-2 py-0.5 text-xs text-muted-foreground" title={`Linked through ${junction.label}`}>
                    <Link2 className="size-3" />
                    via {junction.label}
                </span>
                {isEditable && (
                    <Button variant="ghost" size="sm" className="h-7 px-2 text-xs ml-auto" onClick={() => setPicking(true)}>
                        <Plus className="size-3 mr-1" /> Add link
                    </Button>
                )}
            </div>

            <div className="flex-1 min-h-0 overflow-auto">
                {error && <div className="p-3 text-sm text-destructive">Error: {(error as Error).message}</div>}
                {isLoading && (
                    <div className="flex items-center gap-2 p-3 text-sm text-muted-foreground">
                        <Loader2 className="size-4 animate-spin" /> Loading…
                    </div>
                )}
                {!isLoading && rows.length === 0 && (
                    <div className="p-3 text-sm text-muted-foreground">No linked {target.label}.</div>
                )}
                {rows.length > 0 && (
                    <table className="w-full text-sm">
                        <thead>
                            <tr className="border-b border-border text-left text-xs text-muted-foreground">
                                <th className="px-2 py-1 font-medium">{target.label}</th>
                                {payload.map((c) => (
                                    <th key={c.name} className="px-2 py-1 font-medium">{c.label}</th>
                                ))}
                                {isEditable && <th className="px-2 py-1 w-8" aria-label="actions" />}
                            </tr>
                        </thead>
                        <tbody>
                            {rows.map((row, i) => {
                                const display = targetDisplay(row, m2m, target);
                                return (
                                    <tr key={i} className="border-b border-border/50 hover:bg-muted/30">
                                        <td className="px-2 py-1">
                                            {display ? (
                                                <span className="inline-flex items-center gap-1">
                                                    <Link to={`/${target.name}/${display.id}`} className="text-primary hover:underline">
                                                        {display.label}
                                                    </Link>
                                                    {onOpenColumn && (
                                                        <Button
                                                            variant="ghost"
                                                            size="icon-sm"
                                                            className="size-5"
                                                            onClick={() => onOpenColumn({ tableName: target.name, filterId: display.id })}
                                                            aria-label={`Open ${display.label} in side column`}
                                                            title="Open in side column"
                                                        >
                                                            <Search className="size-3" />
                                                        </Button>
                                                    )}
                                                </span>
                                            ) : (
                                                <span className="text-muted-foreground">—</span>
                                            )}
                                        </td>
                                        {payload.map((c) => (
                                            <td key={c.name} className="px-2 py-1">{formatColumnValue(row[c.name], c)}</td>
                                        ))}
                                        {isEditable && (
                                            <td className="px-2 py-1">
                                                <Button
                                                    variant="ghost"
                                                    size="icon-sm"
                                                    className="size-6 text-muted-foreground hover:text-destructive"
                                                    onClick={() => setDetachRow(row)}
                                                    disabled={detach.isPending}
                                                    aria-label={`Detach ${display?.label ?? 'link'}`}
                                                    title="Detach"
                                                >
                                                    <Unlink className="size-3.5" />
                                                </Button>
                                            </td>
                                        )}
                                    </tr>
                                );
                            })}
                        </tbody>
                    </table>
                )}
                {rows.length >= M2M_ROW_LIMIT && (
                    <div className="px-3 py-2 text-xs text-muted-foreground italic border-t border-border">
                        Showing the first {M2M_ROW_LIMIT} links. Detach some or narrow the parent selection to see more.
                    </div>
                )}
            </div>

            {picking && (
                <TargetPicker
                    target={target}
                    junction={junction}
                    m2m={m2m}
                    parentRowId={parentRowId}
                    onClose={() => setPicking(false)}
                    onLinked={invalidate}
                />
            )}
            <ConfirmDialog
                open={detachRow !== null}
                onOpenChange={(open) => { if (!open) setDetachRow(null); }}
                title="Detach this link?"
                description={<p>This removes the link row. The {target.label} record itself is not deleted.</p>}
                confirmLabel="Detach"
                variant="destructive"
                isPending={detach.isPending}
                onConfirm={async () => {
                    try {
                        if (detachRow) await handleDetach(detachRow);
                    } catch (e) {
                        toast(`Detach failed: ${(e as Error).message}`, 'error');
                    } finally {
                        setDetachRow(null);
                    }
                }}
            />
        </div>
    );
}

interface TargetPickerProps {
    target: Table;
    junction: Table;
    m2m: ManyToManyJoin;
    parentRowId: string;
    onClose: () => void;
    onLinked: () => Promise<void> | void;
}

/** Dialog that lists target rows and inserts a junction row for the chosen one. */
function TargetPicker({ target, junction, m2m, parentRowId, onClose, onLinked }: TargetPickerProps) {
    const fetcher = useFetcher();
    // The two bridge FK columns are the only fields we insert; pass them as the
    // mutation's edit columns so their string route ids get numeric coercion.
    const fkColumns = junction.columns
        .filter((c) => m2m.junctionSourceColumnNames.includes(c.name) || m2m.junctionTargetColumnNames.includes(c.name))
        .map((column) => ({ column }));
    const attach = useTableMutation(junction, fkColumns, []);
    const [search, setSearch] = useState('');
    const [debounced, setDebounced] = useState('');

    // Debounce so each keystroke doesn't fire a query; the search runs
    // server-side so matches beyond the fetch limit stay findable.
    useEffect(() => {
        const t = setTimeout(() => setDebounced(search), 300);
        return () => clearTimeout(t);
    }, [search]);

    const term = debounced.trim();
    const { query, idColumn, serverSearch } = m2mTargetPickerPlan(target, m2m, term);
    const pickerLimit = serverSearch ? M2M_PICKER_SERVER_LIMIT : M2M_PICKER_CLIENT_LIMIT;

    const { data, isLoading } = useQuery({
        // Only vary the query by term when the server actually filters; otherwise
        // one fetch is reused and filtering happens client-side.
        queryKey: ['m2mTargets', target.name, serverSearch ? term : ''],
        queryFn: ({ signal }) => fetcher.query<Record<string, { data: Record<string, unknown>[] }>>(
            query,
            serverSearch && term ? { limit: pickerLimit, search: term } : { limit: pickerLimit },
            { signal },
        ),
    });

    const allRows = data?.[target.name]?.data ?? [];
    const rows = serverSearch || !term
        ? allRows
        : allRows.filter((r) => String(r.label ?? r[idColumn] ?? '').toLowerCase().includes(term.toLowerCase()));
    // Truncation is about the fetched window (allRows), not the client-filtered view.
    const windowFull = allRows.length >= pickerLimit;

    const handlePick = useCallback(async (targetId: string) => {
        await attach.insert(attachJunctionDetail(m2m, parentRowId, targetId));
        await onLinked();
        onClose();
    }, [attach, m2m, parentRowId, onLinked, onClose]);

    return (
        <Dialog open onOpenChange={(o) => { if (!o) onClose(); }}>
            <DialogContent className="max-w-md">
                <DialogHeader>
                    <DialogTitle>Add {target.label} link</DialogTitle>
                </DialogHeader>
                <div className="flex items-center gap-2">
                    <Search className="size-4 text-muted-foreground" />
                    <Input autoFocus placeholder={`Search ${target.label}…`} value={search} onChange={(e) => setSearch(e.target.value)} />
                </div>
                {attach.error && <p className="text-sm text-destructive">{attach.error.message}</p>}
                <div className="max-h-72 overflow-auto border border-border rounded-md">
                    {isLoading && (
                        <div className="flex items-center gap-2 p-3 text-sm text-muted-foreground">
                            <Loader2 className="size-4 animate-spin" /> Loading…
                        </div>
                    )}
                    {!isLoading && rows.length === 0 && (
                        <div className="p-3 text-sm text-muted-foreground">No matches.</div>
                    )}
                    {rows.map((r, i) => {
                        const id = String(r[idColumn] ?? '');
                        const label = r.label != null ? String(r.label) : id;
                        return (
                            <button
                                key={i}
                                type="button"
                                className="block w-full text-left px-3 py-1.5 text-sm hover:bg-muted/50 disabled:opacity-50"
                                disabled={attach.isPending}
                                onClick={() => handlePick(id)}
                            >
                                {label}
                            </button>
                        );
                    })}
                    {!isLoading && windowFull && (
                        <div className="px-3 py-1.5 text-xs text-muted-foreground italic border-t border-border">
                            {serverSearch
                                ? `Showing the first ${pickerLimit} matches — type to narrow.`
                                : `Showing the first ${pickerLimit} rows; some may not be listed.`}
                        </div>
                    )}
                </div>
            </DialogContent>
        </Dialog>
    );
}
