import { useState } from 'react';
import { Table, Join } from '../types/schema';
import { useSchema } from '../hooks/useSchema';
import { DataDataTable } from '../data-data-table';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';
import { ChevronDown, ChevronUp, PanelRight, X } from 'lucide-react';
import type { ColumnPanel } from '../data-panel';

interface DetailPanelProps {
    parentTable: Table;
    selectedRowId: string;
    onClose?: () => void;
    onOpenColumn?: (panel: ColumnPanel) => void;
}

export function DetailPanel({ parentTable, selectedRowId, onClose, onOpenColumn }: DetailPanelProps) {
    const schema = useSchema();
    const joins = parentTable.multiJoins;
    const [activeTab, setActiveTab] = useState<string>(joins[0]?.destinationTable ?? '');
    const [collapsed, setCollapsed] = useState(false);

    if (joins.length === 0 || !schema.data) return null;

    const activeJoin = joins.find((j) => j.destinationTable === activeTab) ?? joins[0];
    const childTable = schema.findTable(activeJoin.destinationTable);

    if (!childTable) return null;

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
                    {joins.map((j: Join) => {
                        const table = schema.findTable(j.destinationTable);
                        const label = table?.label ?? j.destinationTable;
                        return (
                            <span key={j.destinationTable} className="group/tab inline-flex items-center">
                                <Button
                                    variant={activeTab === j.destinationTable ? 'secondary' : 'ghost'}
                                    size="sm"
                                    className={cn(
                                        'text-xs h-7 px-2.5',
                                        activeTab === j.destinationTable && 'font-semibold'
                                    )}
                                    onClick={() => setActiveTab(j.destinationTable)}
                                >
                                    {label}
                                </Button>
                                {onOpenColumn && (
                                    <Button
                                        variant="ghost"
                                        size="icon-sm"
                                        className="opacity-0 group-hover/tab:opacity-100 size-5 shrink-0"
                                        onClick={() => onOpenColumn({
                                            tableName: j.destinationTable,
                                            filterTable: parentTable.name,
                                            filterId: selectedRowId,
                                            filterColumn: j.destinationColumnNames[0],
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
                    <DataDataTable
                        key={`${activeJoin.destinationTable}-${selectedRowId}`}
                        table={childTable}
                        id={selectedRowId}
                        filterColumn={activeJoin.destinationColumnNames[0]}
                    />
                </div>
            )}
        </div>
    );
}
