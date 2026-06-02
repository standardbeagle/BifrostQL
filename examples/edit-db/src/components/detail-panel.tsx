import { useState } from 'react';
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

    if (tabs.length === 0 || !schema.data) return null;

    const activeTab = tabs.find((t) => t.key === activeKey) ?? tabs[0];

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
                                            // Single FK: pin to the direct FK column on the child.
                                            // Composite FK: omit filterColumn so query-builder
                                            // emits a composite-PK nested filter on the parent.
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
                    {activeTab.kind === 'child' ? (
                        <DataDataTable
                            key={`${activeTab.key}-${selectedRowId}`}
                            table={schema.findTable(activeTab.join.destinationTable)!}
                            id={selectedRowId}
                            // Composite multi-joins route through tableFilter so the parent
                            // composite PK becomes a nested {and: [...]} filter; single FKs
                            // keep the legacy direct-FK-column shape.
                            {...(isComposite(activeTab.join)
                                ? { tableFilter: parentTable.name }
                                : { filterColumn: activeTab.join.destinationColumnNames[0] })}
                        />
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
