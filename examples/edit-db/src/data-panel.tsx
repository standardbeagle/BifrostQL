import React from 'react'
import { DataDataTable } from './data-data-table';
import { useParams } from './hooks/usePath';
import { useSchema } from './hooks/useData';

function getTable(data: any[], tableName: string) {
    const table = data.find((x: { name: string | undefined; }) => x.name == tableName);
    return table;
}

export function DataPanel() {
    const params = useParams();
    const { table, id, filterTable } = params as { table: string, id: string, filterTable: string };
    const { loading, error, data } = useSchema();

    if (!table) return <div>Table missing</div>;
    if (loading) return <div>Loading...</div>;
    if (error) return <div>Error: {error.message}</div>;

    return <DataDataTable table={getTable(data, table)} id={id} tableFilter={filterTable} />;
}