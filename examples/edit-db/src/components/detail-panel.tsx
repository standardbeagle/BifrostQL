import { useState } from 'react';
import { Table, Join } from '../types/schema';
import { useSchema } from '../hooks/useSchema';
import { DataDataTable } from '../data-data-table';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';
import { ChevronDown, ChevronUp } from 'lucide-react';

interface DetailPanelProps {
    parentTable: Table;
    selectedRowId: string;
}

export function DetailPanel({ parentTable, selectedRowId }: DetailPanelProps) {
    const schema = useSchema();
    const joins = parentTable.multiJoins;
    const [activeTab, setActiveTab] = useState<string>(joins[0]?.destinationTable ?? '');
    const [collapsed, setCollapsed] = useState(false);

    if (joins.length === 0 || !schema.data) return null;

    const activeJoin = joins.find((j) => j.destinationTable === activeTab) ?? joins[0];
    const childTable = schema.findTable(activeJoin.destinationTable);

    if (!childTable) return null;

    return (
        <div className="border-t-2 border-primary/20 flex flex-col min-h-0">
            <div className="flex items-center gap-1 px-2 py-1 bg-muted/30 border-b border-border shrink-0">
                <Button
                    variant="ghost"
                    size="icon-sm"
                    onClick={() => setCollapsed(!collapsed)}
                    aria-label={collapsed ? 'Expand detail panel' : 'Collapse detail panel'}
                >
                    {collapsed ? <ChevronUp className="size-3.5" /> : <ChevronDown className="size-3.5" />}
                </Button>
                <span className="text-xs text-muted-foreground mr-1">Detail:</span>
                <div className="flex items-center gap-0.5 overflow-x-auto">
                    {joins.map((j: Join) => {
                        const table = schema.findTable(j.destinationTable);
                        const label = table?.label ?? j.destinationTable;
                        return (
                            <Button
                                key={j.destinationTable}
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
                        );
                    })}
                </div>
            </div>
            {!collapsed && (
                <div className="flex-1 min-h-0 overflow-auto">
                    <DataDataTable
                        key={`${activeJoin.destinationTable}-${selectedRowId}`}
                        table={childTable}
                        id={selectedRowId}
                        tableFilter={parentTable.name}
                    />
                </div>
            )}
        </div>
    );
}
