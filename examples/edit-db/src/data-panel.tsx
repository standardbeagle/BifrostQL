import { useState, useMemo } from 'react';
import { DataDataTable } from './data-data-table';
import { DetailPanel } from './components/detail-panel';
import { useParams } from './hooks/usePath';
import { useSchema } from './hooks/useSchema';
import { Table } from './types/schema';
import { Loader2 } from 'lucide-react';
import { Alert, AlertDescription } from '@/components/ui/alert';

function getTable(data: Table[], tableName: string): Table | undefined {
    return data.find((x) => x.name === tableName);
}

export function DataPanel() {
    const params = useParams();
    const { table: tableName, id, filterTable } = params as { table: string; id: string; filterTable: string };
    const { loading, error, data } = useSchema();
    const [selectedRowId, setSelectedRowId] = useState<string | null>(null);

    const table = useMemo(() => data ? getTable(data, tableName) : undefined, [data, tableName]);
    const hasMultiJoins = (table?.multiJoins?.length ?? 0) > 0;

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
            <div className={hasMultiJoins && selectedRowId ? 'flex-1 min-h-0 max-h-[50%]' : 'flex-1 min-h-0'}>
                <DataDataTable
                    table={table}
                    id={id}
                    tableFilter={filterTable}
                    selectedRowId={hasMultiJoins ? selectedRowId : undefined}
                    onRowSelect={hasMultiJoins ? setSelectedRowId : undefined}
                />
            </div>
            {hasMultiJoins && selectedRowId && (
                <div className="flex-1 min-h-0 overflow-auto">
                    <DetailPanel
                        parentTable={table}
                        selectedRowId={selectedRowId}
                    />
                </div>
            )}
        </div>
    );
}
