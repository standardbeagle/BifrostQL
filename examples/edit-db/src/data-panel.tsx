import { useState, useMemo, useCallback, useEffect, useRef, type ReactNode } from 'react';
import { DataDataTable } from './data-data-table';
import { DetailPanel } from './components/detail-panel';
import { useParams } from './hooks/usePath';
import { useSchema } from './hooks/useSchema';
import { useColumnNavRegister } from './hooks/useColumnNav';
import { Table } from './types/schema';
import { Loader2, X, ChevronRight, ChevronDown, Home } from 'lucide-react';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';
import {
    pushDrillFrame,
    popDrillFramesTo,
    buildDrillCrumbs,
    type DrillFrame,
} from './lib/drill-stack';
import { detailTabs } from './lib/m2m';

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
    // Parent/child drill-down ("stacking") mode. When off, FK/multi-join cells
    // render as flat values and the grid behaves as a standard single-table view.
    const [stackingEnabled, setStackingEnabled] = useState(true);
    // Levels the user manually re-expanded. By default every ancestor (a level
    // with a deeper level below it) auto-collapses to its selected row; the
    // deepest/active level is always full. Keyed by level: -1 = main grid,
    // 0..n = drill columns.
    const [expandedLevels, setExpandedLevels] = useState<Set<number>>(new Set());

    // Reset the drill stack whenever the root table changes so we don't carry
    // ghost breadcrumbs across unrelated navigations.
    const lastTableRef = useRef(tableName);
    if (tableName !== lastTableRef.current) {
        lastTableRef.current = tableName;
        if (openColumns.length > 0) setOpenColumns([]);
        if (expandedLevels.size > 0) setExpandedLevels(new Set());
    }

    const toggleLevel = useCallback((key: number) => {
        setExpandedLevels((prev) => {
            const next = new Set(prev);
            next.has(key) ? next.delete(key) : next.add(key);
            return next;
        });
    }, []);

    const { register, unregister } = useColumnNavRegister();
    const mainRef = useRef<HTMLDivElement | null>(null);
    const columnRefsMap = useRef<Map<number, HTMLElement | null>>(new Map());

    const table = useMemo(() => data ? getTable(data, tableName) : undefined, [data, tableName]);
    const hasMultiJoins = table ? detailTabs(table).length > 0 : false;

    const handleOpenColumn = useCallback((panel: ColumnPanel) => {
        setOpenColumns((prev) => pushDrillFrame(prev, panel));
    }, []);

    // Switching to flat grid mode collapses any open drill columns so the view
    // doesn't strand side panels the user can no longer extend.
    const handleToggleStacking = useCallback((next: boolean) => {
        setStackingEnabled(next);
        if (!next) setOpenColumns([]);
    }, []);

    // Gating the open-column handler at the source removes the per-cell drill
    // buttons in flat mode (the grid only renders them when a handler exists).
    const drillHandler = stackingEnabled ? handleOpenColumn : undefined;

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

    const mainGrid = (
        <DataDataTable
            table={table}
            id={id}
            tableFilter={filterTable}
            selectedRowId={hasMultiJoins ? selectedRowId : undefined}
            onRowSelect={hasMultiJoins ? setSelectedRowId : undefined}
            onOpenColumn={drillHandler}
            stackingEnabled={stackingEnabled}
            onToggleStacking={handleToggleStacking}
        />
    );

    // No drill stack open: keep the standard single-table view (with the m2m
    // detail panel below when applicable). Unchanged behavior.
    if (openColumns.length === 0) {
        return (
            <div className="flex flex-col flex-1 min-h-0">
                <div ref={mainRef} className="flex flex-col flex-1 min-h-0 min-w-0">
                    <div className={hasMultiJoins && selectedRowId ? 'flex-1 min-h-0 max-h-[50%] overflow-hidden flex flex-col' : 'flex-1 min-h-0 overflow-hidden flex flex-col'}>
                        {mainGrid}
                    </div>
                    {hasMultiJoins && selectedRowId && (
                        <div className="flex-1 min-h-0 overflow-hidden flex flex-col">
                            <DetailPanel
                                parentTable={table}
                                selectedRowId={selectedRowId}
                                onClose={() => setSelectedRowId(null)}
                                onOpenColumn={drillHandler}
                            />
                        </div>
                    )}
                </div>
            </div>
        );
    }

    // Multi-generational drill stack: every generation stacks vertically and the
    // whole stack shares one scrollbar. Each generation keeps a min-height when
    // expanded; ancestors auto-collapse to their selected row (the row that was
    // drilled into the next generation) unless the user re-expands them.
    const lastIndex = openColumns.length - 1;
    const isExpanded = (key: number, active: boolean) => active || expandedLevels.has(key);

    return (
        <div className="flex flex-col flex-1 min-h-0">
            <DrillBreadcrumb
                crumbs={crumbs}
                onCrumbClick={handleCrumbClick}
                activeIndex={openColumns.length - 1}
            />
            <div className="flex-1 min-h-0 overflow-y-auto flex flex-col gap-0.5 p-0.5">
                <StackLevel
                    title={table.label}
                    selectedRowId={openColumns[0]?.filterId}
                    expanded={isExpanded(-1, false)}
                    active={false}
                    onToggle={() => toggleLevel(-1)}
                    onRef={(el) => { mainRef.current = el; columnRefsMap.current.set(0, el); }}
                >
                    {mainGrid}
                </StackLevel>
                {openColumns.map((col, index) => {
                    const active = index === lastIndex;
                    return (
                        <StackLevel
                            key={`${col.tableName}-${col.filterId ?? ''}-${col.filterColumn ?? ''}-${index}`}
                            title={getTable(data!, col.tableName)?.label ?? col.tableName}
                            selectedRowId={openColumns[index + 1]?.filterId}
                            expanded={isExpanded(index, active)}
                            active={active}
                            onToggle={() => toggleLevel(index)}
                            onClose={() => handleCloseColumn(index)}
                            onRef={(el) => { columnRefsMap.current.set(index + 1, el); }}
                        >
                            <DataDataTable
                                table={getTable(data!, col.tableName)!}
                                id={col.filterId}
                                tableFilter={col.filterTable}
                                filterColumn={col.filterColumn}
                                onOpenColumn={handleOpenColumn}
                            />
                        </StackLevel>
                    );
                })}
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

interface StackLevelProps {
    /** Table label shown in the level header. */
    title: string;
    /** Id of this level's selected row (the row drilled into the next level);
     *  shown as a chip when collapsed. */
    selectedRowId?: string;
    /** Expanded → full grid; collapsed → header-only summary. */
    expanded: boolean;
    /** The deepest level — always expanded, badged "active". */
    active: boolean;
    onToggle: () => void;
    /** Close (only for drill columns, not the main level). */
    onClose?: () => void;
    onRef?: (el: HTMLDivElement | null) => void;
    children: ReactNode;
}

/**
 * One generation in the vertical drill stack: a header (expand/collapse, label,
 * selected-row chip, close) plus the grid when expanded. Collapsed it shrinks to
 * the header so deep stacks stay navigable under one outer scrollbar.
 */
function StackLevel({ title, selectedRowId, expanded, active, onToggle, onClose, onRef, children }: StackLevelProps) {
    return (
        <div ref={onRef} className="shrink-0 flex flex-col border border-border rounded-sm overflow-hidden">
            <div className="flex items-center gap-1 px-2 py-1 bg-muted/30 border-b border-border shrink-0">
                <button
                    type="button"
                    onClick={onToggle}
                    className="inline-flex items-center justify-center size-5 shrink-0 text-muted-foreground hover:text-foreground"
                    aria-expanded={expanded}
                    aria-label={expanded ? 'Collapse table' : 'Expand table'}
                    title={expanded ? 'Collapse' : 'Expand'}
                >
                    {expanded ? <ChevronDown className="size-3.5" /> : <ChevronRight className="size-3.5" />}
                </button>
                <span className="text-xs font-medium truncate">{title}</span>
                {active && (
                    <span className="text-[10px] uppercase tracking-wide text-primary/70 shrink-0">active</span>
                )}
                {!expanded && selectedRowId && (
                    <span className="text-xs text-muted-foreground truncate">#{selectedRowId}</span>
                )}
                <div className="flex-1" />
                {onClose && (
                    <Button
                        variant="ghost"
                        size="icon-sm"
                        className="shrink-0"
                        onClick={onClose}
                        aria-label="Close column"
                        title="Close column"
                    >
                        <X className="size-3.5" />
                    </Button>
                )}
            </div>
            {expanded && (
                <div className="flex flex-col min-h-[18rem] max-h-[34rem]">
                    {children}
                </div>
            )}
        </div>
    );
}
