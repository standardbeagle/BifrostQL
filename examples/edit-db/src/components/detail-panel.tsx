import { useState, useEffect } from 'react';
import { Table } from '../types/schema';
import { useSchema } from '../hooks/useSchema';
import { DataDataTable } from '../data-data-table';
import { M2mPanel } from './m2m-panel';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';
import { ChevronDown, ChevronUp, PanelRight, X } from 'lucide-react';
import type { ColumnPanel } from '../data-panel';
import { isComposite } from '../lib/fk';
import { detailTabs, type DetailTab } from '../lib/m2m';

interface DetailPanelProps {
    parentTable: Table;
    selectedRowId: string;
    onClose?: () => void;
    onOpenColumn?: (panel: ColumnPanel) => void;
}

/** A child collection tab's destination table; a m2m tab's target table. */
function tabTargetTable(tab: DetailTab): string {
    return tab.kind === 'child' ? tab.join.destinationTable : tab.m2m.targetTable;
}

export function DetailPanel({ parentTable, selectedRowId, onClose, onOpenColumn }: DetailPanelProps) {
    const schema = useSchema();
    const tabs = detailTabs(parentTable);
    const [activeKey, setActiveKey] = useState<string>(tabs[0]?.key ?? '');
    const [collapsed, setCollapsed] = useState(false);
    // Selection within this panel's child table, used to drill the next level
    // deeper (a nested DetailPanel beneath). Cleared whenever the parent row or
    // active tab changes so a stale id can't filter the wrong table.
    const [childSelectedRowId, setChildSelectedRowId] = useState<string | null>(null);

    const activeTab = tabs.find((t) => t.key === activeKey) ?? tabs[0];

    useEffect(() => { setChildSelectedRowId(null); }, [activeKey, selectedRowId]);

    if (tabs.length === 0 || !schema.data) return null;

    // Child-collection tabs can recurse; m2m tabs render the junction-skipping panel.
    const childTable = activeTab.kind === 'child'
        ? schema.findTable(activeTab.join.destinationTable)
        : undefined;
    // When the child table is itself a parent of other tables, selecting one of
    // its rows opens the next level beneath (recursion terminates at leaf tables).
    const childHasMultiJoins = (childTable?.multiJoins?.length ?? 0) > 0;

    return (
        <div className="border-t-2 border-primary/20 flex flex-col min-h-0 flex-1 overflow-hidden">
            <div className="flex items-center gap-1 px-2 py-1 bg-muted/30 border-b border-border shrink-0">
                <Button
                    variant="ghost"
                    size="icon-sm"
                    onClick={() => setCollapsed(!collapsed)}
                    aria-label={collapsed ? 'Expand detail panel' : 'Collapse detail panel'}
                    title={collapsed ? 'Expand detail panel' : 'Collapse detail panel'}
                >
                    {collapsed ? <ChevronUp className="size-3.5" /> : <ChevronDown className="size-3.5" />}
                </Button>
                <span className="text-xs text-muted-foreground mr-1">Detail:</span>
                <div className="flex items-center gap-0.5 overflow-x-auto">
                    {tabs.map((tab) => {
                        const table = schema.findTable(tabTargetTable(tab));
                        const label = table?.label ?? tabTargetTable(tab);
                        const isActive = tab.key === activeTab.key;
                        return (
                            <span key={tab.key} className="group/tab inline-flex items-center">
                                <Button
                                    variant={isActive ? 'secondary' : 'ghost'}
                                    size="sm"
                                    className={cn('text-xs h-7 px-2.5', isActive && 'font-semibold')}
                                    onClick={() => setActiveKey(tab.key)}
                                >
                                    {label}
                                </Button>
                                {onOpenColumn && tab.kind === 'child' && (
                                    <Button
                                        variant="ghost"
                                        size="icon-sm"
                                        className="opacity-0 group-hover/tab:opacity-100 size-5 shrink-0"
                                        onClick={() => onOpenColumn({
                                            tableName: tab.join.destinationTable,
                                            filterTable: parentTable.name,
                                            filterId: selectedRowId,
                                            // filterColumn (the child destination column) only
                                            // disambiguates when the parent has multiple
                                            // multi-joins to the same child; the MODEL B
                                            // traversal scopes by the parent PK regardless.
                                            ...(isComposite(tab.join)
                                                ? {}
                                                : { filterColumn: tab.join.destinationColumnNames[0] }),
                                        })}
                                        aria-label="Open in side column"
                                        title="Open in side column"
                                    >
                                        <PanelRight className="size-3" />
                                    </Button>
                                )}
                            </span>
                        );
                    })}
                </div>
                {onClose && (
                    <Button
                        variant="ghost"
                        size="icon-sm"
                        onClick={onClose}
                        className="ml-auto shrink-0"
                        aria-label="Close detail panel"
                        title="Close detail panel"
                    >
                        <X className="size-3.5" />
                    </Button>
                )}
            </div>
            {!collapsed && (
                <div className="flex-1 min-h-0 overflow-hidden flex flex-col">
                    {activeTab.kind === 'child' && !childTable ? (
                        // The child's destination table isn't in the published schema
                        // (e.g. hidden by visibility metadata) — show a notice instead
                        // of dereferencing an undefined table and crashing.
                        <div className="p-4 text-sm text-muted-foreground">
                            This related table is not available.
                        </div>
                    ) : activeTab.kind === 'child' ? (
                        <>
                            <div className={childHasMultiJoins && childSelectedRowId
                                ? 'flex-1 min-h-0 max-h-[50%] overflow-hidden flex flex-col'
                                : 'flex-1 min-h-0 overflow-hidden flex flex-col'}>
                                <DataDataTable
                                    key={`${activeTab.key}-${selectedRowId}`}
                                    table={childTable!}
                                    id={selectedRowId}
                                    // MODEL B: always traverse the parent so the server scopes the
                                    // child rows (including any polymorphic discriminator). filterColumn
                                    // only disambiguates when several multi-joins target the same child.
                                    tableFilter={parentTable.name}
                                    {...(isComposite(activeTab.join)
                                        ? {}
                                        : { filterColumn: activeTab.join.destinationColumnNames[0] })}
                                    selectedRowId={childHasMultiJoins ? childSelectedRowId : undefined}
                                    onRowSelect={childHasMultiJoins ? setChildSelectedRowId : undefined}
                                    onOpenColumn={onOpenColumn}
                                />
                            </div>
                            {childHasMultiJoins && childSelectedRowId && (
                                <div className="flex-1 min-h-0 overflow-hidden flex flex-col">
                                    <DetailPanel
                                        parentTable={childTable!}
                                        selectedRowId={childSelectedRowId}
                                        onClose={() => setChildSelectedRowId(null)}
                                        onOpenColumn={onOpenColumn}
                                    />
                                </div>
                            )}
                        </>
                    ) : (
                        <M2mPanel
                            key={`${activeTab.key}-${selectedRowId}`}
                            parentTable={parentTable}
                            m2m={activeTab.m2m}
                            parentRowId={selectedRowId}
                            onOpenColumn={onOpenColumn}
                        />
                    )}
                </div>
            )}
        </div>
    );
}
