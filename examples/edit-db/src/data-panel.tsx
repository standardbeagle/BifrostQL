import { useState, useMemo, useCallback } from 'react';
import { DataDataTable } from './data-data-table';
import { DetailPanel } from './components/detail-panel';
import { useParams } from './hooks/usePath';
import { useSchema } from './hooks/useSchema';
import { Table } from './types/schema';
import { Loader2, X } from 'lucide-react';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { Button } from '@/components/ui/button';

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
    const [openColumns, setOpenColumns] = useState<ColumnPanel[]>([]);

    const table = useMemo(() => data ? getTable(data, tableName) : undefined, [data, tableName]);
    const hasMultiJoins = (table?.multiJoins?.length ?? 0) > 0;

    const handleOpenColumn = useCallback((panel: ColumnPanel) => {
        setOpenColumns((prev) => [...prev, panel]);
    }, []);

    const handleCloseColumn = useCallback((index: number) => {
        setOpenColumns((prev) => prev.filter((_, i) => i !== index));
    }, []);

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
        <div className="flex flex-1 min-h-0 gap-0.5">
            <div className="flex flex-col flex-1 min-h-0 min-w-0">
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
                    key={`${col.tableName}-${col.filterId ?? index}`}
                    panel={col}
                    onClose={() => handleCloseColumn(index)}
                />
            ))}
        </div>
    );
}

function SideColumn({ panel, onClose }: { panel: ColumnPanel; onClose: () => void }) {
    const { data } = useSchema();
    const table = useMemo(() => data ? getTable(data, panel.tableName) : undefined, [data, panel.tableName]);

    if (!table) return null;

    return (
        <div className="flex flex-col flex-1 min-h-0 min-w-0 border-l border-border">
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
                />
            </div>
        </div>
    );
}
