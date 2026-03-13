import { DataDataTable } from './data-data-table';
import { useParams } from './hooks/usePath';
import { useSchema } from './hooks/useSchema';
import { Loader2 } from 'lucide-react';
import { Alert, AlertDescription } from '@/components/ui/alert';

function getTable(data: any[], tableName: string) {
    const table = data.find((x: { name: string | undefined; }) => x.name == tableName);
    return table;
}

export function DataPanel() {
    const params = useParams();
    const { table, id, filterTable } = params as { table: string, id: string, filterTable: string };
    const { loading, error, data } = useSchema();

    if (!table) return <div className="p-5 text-center text-muted-foreground">Table missing</div>;
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

    return <DataDataTable table={getTable(data, table)} id={id} tableFilter={filterTable} />;
}
