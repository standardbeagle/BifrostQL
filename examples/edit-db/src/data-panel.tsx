import { useState, useMemo, useCallback, useEffect, useRef } from 'react';
import { DataDataTable } from './data-data-table';
import { DetailPanel } from './components/detail-panel';
import { useParams } from './hooks/usePath';
import { useSchema } from './hooks/useSchema';
import { useColumnNavRegister } from './hooks/useColumnNav';
import { Table } from './types/schema';
import { Loader2, X, ChevronRight, Home } from 'lucide-react';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';
import {
    pushDrillFrame,
    popDrillFramesTo,
    buildDrillCrumbs,
    type DrillFrame,
} from './lib/drill-stack';

/**
 * A side-panel drill frame. `filterTable` + `filterId` + `filterColumn`
 * describe the FK scoping applied to `tableName`'s rows — they're optional so
 * the same shape can also represent a simple "open table X" panel.
 */
export interface ColumnPanel {
    tableName: string;
    filterTable?: string;
    filterId?: string;
    filterColumn?: string;
}

function getTable(data: Table[], tableName: string): Table | undefined {
    return data.find((x) => x.name === tableName);
}

export function DataPanel() {
    const params = useParams();
    const { table: tableName, id, filterTable } = params as { table: string; id: string; filterTable: string };
    const { loading, error, data } = useSchema();
    const [selectedRowId, setSelectedRowId] = useState<string | null>(null);
    const [openColumns, setOpenColumns] = useState<DrillFrame[]>([]);

    // Reset the drill stack whenever the root table changes so we don't carry
    // ghost breadcrumbs across unrelated navigations.
    const lastTableRef = useRef(tableName);
    if (tableName !== lastTableRef.current) {
        lastTableRef.current = tableName;
        if (openColumns.length > 0) setOpenColumns([]);
    }

    const { register, unregister } = useColumnNavRegister();
    const mainRef = useRef<HTMLDivElement>(null);
    const columnRefsMap = useRef<Map<number, HTMLElement | null>>(new Map());

    const table = useMemo(() => data ? getTable(data, tableName) : undefined, [data, tableName]);
    const hasMultiJoins = (table?.multiJoins?.length ?? 0) > 0;

    const handleOpenColumn = useCallback((panel: ColumnPanel) => {
        setOpenColumns((prev) => pushDrillFrame(prev, panel));
    }, []);

    const handleCloseColumn = useCallback((index: number) => {
        setOpenColumns((prev) => prev.filter((_, i) => i !== index));
    }, []);

    // Truncate the stack back to (not including) `index`. Pass -1 to return
    // to the main table view (empties the stack entirely).
    const handleCrumbClick = useCallback((index: number) => {
        setOpenColumns((prev) => popDrillFramesTo(prev, index + 1));
    }, []);

    const crumbs = useMemo(
        () => buildDrillCrumbs(tableName ?? '', openColumns, (name) => {
            if (!data) return undefined;
            return getTable(data, name)?.label;
        }),
        [tableName, openColumns, data]
    );

    useEffect(() => {
        const refs = columnRefsMap.current;
        refs.set(0, mainRef.current);
        for (const key of refs.keys()) {
            if (key > openColumns.length) refs.delete(key);
        }
        register({
            mainTable: tableName,
            columns: openColumns,
            columnRefs: refs,
            onClose: handleCloseColumn,
        });
        return () => unregister();
    }, [tableName, openColumns, register, unregister, handleCloseColumn]);

    if (!tableName) return <div className="p-5 text-center text-muted-foreground">Table missing</div>;
    if (loading) return (
        <div className="flex items-center justify-center gap-2 p-5">
            <Loader2 className="size-4 animate-spin text-muted-foreground" />
            <span className="text-sm text-muted-foreground">Loading...</span>
        </div>
    );
    if (error) return (
        <Alert variant="destructive" className="m-2">
            <AlertDescription>Error: {error.message}</AlertDescription>
        </Alert>
    );
    if (!table) return <div className="p-5 text-center text-muted-foreground">Table not found</div>;

    return (
        <div className="flex flex-col flex-1 min-h-0">
            {openColumns.length > 0 && (
                <DrillBreadcrumb
                    crumbs={crumbs}
                    onCrumbClick={handleCrumbClick}
                    activeIndex={openColumns.length - 1}
                />
            )}
            <div className="flex flex-1 min-h-0 gap-0.5">
                <div ref={mainRef} className="flex flex-col flex-1 min-h-0 min-w-0">
                    <div className={hasMultiJoins && selectedRowId ? 'flex-1 min-h-0 max-h-[50%] overflow-hidden flex flex-col' : 'flex-1 min-h-0 overflow-hidden flex flex-col'}>
                        <DataDataTable
                            table={table}
                            id={id}
                            tableFilter={filterTable}
                            selectedRowId={hasMultiJoins ? selectedRowId : undefined}
                            onRowSelect={hasMultiJoins ? setSelectedRowId : undefined}
                            onOpenColumn={handleOpenColumn}
                        />
                    </div>
                    {hasMultiJoins && selectedRowId && (
                        <div className="flex-1 min-h-0 overflow-hidden flex flex-col">
                            <DetailPanel
                                parentTable={table}
                                selectedRowId={selectedRowId}
                                onClose={() => setSelectedRowId(null)}
                                onOpenColumn={handleOpenColumn}
                            />
                        </div>
                    )}
                </div>
                {openColumns.map((col, index) => (
                    <SideColumn
                        key={`${col.tableName}-${col.filterId ?? ''}-${col.filterColumn ?? ''}-${index}`}
                        panel={col}
                        onClose={() => handleCloseColumn(index)}
                        onOpenColumn={handleOpenColumn}
                        onRef={(el) => { columnRefsMap.current.set(index + 1, el); }}
                    />
                ))}
            </div>
        </div>
    );
}

interface DrillBreadcrumbProps {
    crumbs: ReturnType<typeof buildDrillCrumbs>;
    onCrumbClick: (index: number) => void;
    activeIndex: number;
}

function DrillBreadcrumb({ crumbs, onCrumbClick, activeIndex }: DrillBreadcrumbProps) {
    return (
        <nav
            aria-label="Drill-down breadcrumb"
            className="flex items-center gap-1 px-2 py-1 bg-muted/20 border-b border-border shrink-0 overflow-x-auto"
        >
            {crumbs.map((crumb, i) => {
                const isActive = crumb.index === activeIndex;
                const isFirst = i === 0;
                return (
                    <span key={`${crumb.index}-${crumb.label}`} className="inline-flex items-center gap-1 shrink-0">
                        {!isFirst && <ChevronRight className="size-3 text-muted-foreground shrink-0" />}
                        <Button
                            variant={isActive ? 'secondary' : 'ghost'}
                            size="sm"
                            className={cn('h-6 px-2 text-xs gap-1', isActive && 'font-semibold')}
                            onClick={() => onCrumbClick(crumb.index)}
                            aria-current={isActive ? 'page' : undefined}
                            title={crumb.detail ? `${crumb.label} (${crumb.detail})` : crumb.label}
                        >
                            {isFirst && <Home className="size-3" />}
                            <span className="truncate max-w-[12rem]">{crumb.label}</span>
                            {crumb.detail && (
                                <span className="text-muted-foreground font-normal truncate max-w-[10rem]">
                                    {crumb.detail}
                                </span>
                            )}
                        </Button>
                    </span>
                );
            })}
        </nav>
    );
}

interface SideColumnProps {
    panel: ColumnPanel;
    onClose: () => void;
    onOpenColumn: (panel: ColumnPanel) => void;
    onRef?: (el: HTMLDivElement | null) => void;
}

function SideColumn({ panel, onClose, onOpenColumn, onRef }: SideColumnProps) {
    const { data } = useSchema();
    const table = useMemo(() => data ? getTable(data, panel.tableName) : undefined, [data, panel.tableName]);

    if (!table) return null;

    return (
        <div ref={onRef} className="flex flex-col flex-1 min-h-0 min-w-0 border-l border-border">
            <div className="flex items-center gap-1 px-2 py-1 bg-muted/30 border-b border-border shrink-0">
                <span className="text-xs font-medium truncate flex-1">{table.label}</span>
                <Button
                    variant="ghost"
                    size="icon-sm"
                    onClick={onClose}
                    aria-label="Close column"
                    title="Close column"
                >
                    <X className="size-3.5" />
                </Button>
            </div>
            <div className="flex-1 min-h-0 overflow-hidden flex flex-col">
                <DataDataTable
                    table={table}
                    id={panel.filterId}
                    tableFilter={panel.filterTable}
                    filterColumn={panel.filterColumn}
                    onOpenColumn={onOpenColumn}
                />
            </div>
        </div>
    );
}
