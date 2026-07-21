import { useEffect, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useFetcher } from '@/common/fetcher';
import type { GridGroupMemberRequest, GroupingRow } from '@/lib/grid-grouping';

interface GroupedGridSummaryProps {
    label: string;
    rows: readonly GroupingRow[];
    loading?: boolean;
    error?: Error | null;
    sumLabel?: string;
    memberRequest: (value: unknown) => GridGroupMemberRequest;
}

const GROUP_PAGE_SIZE = 20;

/** Server aggregate groups with local group paging and one expandable, server-filtered member bucket. */
export function GroupedGridSummary({ label, rows, loading, error, sumLabel, memberRequest }: GroupedGridSummaryProps) {
    const fetcher = useFetcher();
    const [page, setPage] = useState(0);
    const [expanded, setExpanded] = useState<GroupingRow | null>(null);
    const request = expanded === null ? null : memberRequest(expanded.value);
    const members = useQuery({
        queryKey: ['groupMembers', request?.query, request?.variables],
        queryFn: ({ signal }) => fetcher.query<Record<string, { total?: number; data?: Record<string, unknown>[] }>>(request!.query, request!.variables, { signal }),
        enabled: request !== null,
    });
    const groupIdentity = rows.map((row) => `${String(row.value)}:${row.count}:${String(row.sum)}`).join('|');
    // Aggregate keys and result sets change together on table/filter navigation.
    // Do not leave an old member bucket expanded beneath the new aggregate list.
    useEffect(() => { setPage(0); setExpanded(null); }, [label, groupIdentity]);
    if (error) return <p role="alert" className="px-2 py-1 text-sm text-destructive">Grouping failed: {error.message}</p>;
    if (loading) return <p className="px-2 py-1 text-sm text-muted-foreground">Loading grouped results…</p>;
    const pageCount = Math.max(1, Math.ceil(rows.length / GROUP_PAGE_SIZE));
    const visible = rows.slice(Math.min(page, pageCount - 1) * GROUP_PAGE_SIZE, (Math.min(page, pageCount - 1) + 1) * GROUP_PAGE_SIZE);
    const memberPage = request ? members.data?.[request.responseKey] : undefined;
    return <div className="border-b px-2 py-2" aria-label={`Grouped by ${label}`}>
        <div className="mb-1 flex items-center justify-between text-sm font-medium"><span>Grouped by {label}</span><span className="text-muted-foreground">{rows.length} groups</span></div>
        <div className="grid grid-cols-[minmax(0,1fr)_auto_auto] gap-x-4 gap-y-1 text-sm">
            {visible.map((row, index) => <div key={`${String(row.value)}-${index}`} className="contents">
                <button type="button" className="text-left underline-offset-2 hover:underline" aria-expanded={expanded === row} onClick={() => setExpanded(expanded === row ? null : row)}>{row.value === null || row.value === undefined ? '(null)' : String(row.value)}</button>
                <span className="tabular-nums text-muted-foreground">{row.count}</span>
                <span className="tabular-nums text-muted-foreground">{sumLabel ? String(row.sum ?? '—') : ''}</span>
            </div>)}
            {rows.length === 0 && <span className="text-muted-foreground">No grouped results.</span>}
        </div>
        {rows.length > GROUP_PAGE_SIZE && <div className="mt-2 flex items-center gap-2 text-xs"><button type="button" disabled={page === 0} onClick={() => setPage((current) => current - 1)}>Previous groups</button><span>Group page {Math.min(page, pageCount - 1) + 1} of {pageCount}</span><button type="button" disabled={page >= pageCount - 1} onClick={() => setPage((current) => current + 1)}>Next groups</button></div>}
        {request && <div className="mt-2 rounded bg-muted/40 p-2 text-xs" aria-live="polite">
            {members.isLoading ? 'Loading group members…' : members.error ? 'Group member query failed.' : <><div>{memberPage?.total ?? 0} matching members</div><pre className="max-h-32 overflow-auto">{JSON.stringify(memberPage?.data ?? [], null, 2)}</pre></>}
        </div>}
    </div>;
}
