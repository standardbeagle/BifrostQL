import type { GroupingRow } from '@/lib/grid-grouping';

interface GroupedGridSummaryProps {
    label: string;
    rows: readonly GroupingRow[];
    loading?: boolean;
    error?: Error | null;
}

/** A small server-backed grouped view; counts are the aggregate response, never client totals. */
export function GroupedGridSummary({ label, rows, loading, error }: GroupedGridSummaryProps) {
    if (error) return <p role="alert" className="px-2 py-1 text-sm text-destructive">Grouping failed: {error.message}</p>;
    if (loading) return <p className="px-2 py-1 text-sm text-muted-foreground">Loading grouped results…</p>;
    return <div className="border-b px-2 py-2" aria-label={`Grouped by ${label}`}>
        <div className="mb-1 text-sm font-medium">Grouped by {label}</div>
        <div className="grid grid-cols-[minmax(0,1fr)_auto] gap-x-4 gap-y-1 text-sm">
            {rows.map((row, index) => <div key={`${String(row.value)}-${index}`} className="contents">
                <span>{row.value === null || row.value === undefined ? '(null)' : String(row.value)}</span>
                <span className="tabular-nums text-muted-foreground">{row.count}</span>
            </div>)}
            {rows.length === 0 && <span className="text-muted-foreground">No grouped results.</span>}
        </div>
    </div>;
}
