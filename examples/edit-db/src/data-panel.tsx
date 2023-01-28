import React from 'react'
import { DataDataTable } from './data-data-table';
import { useParams } from './hooks/usePath';
import { useSchema } from './hooks/useData';

function getTable(data: any[], tableName:string) {
    const table = data.find((x: { name: string | undefined; }) => x.name == tableName);
    return table;
}

export function DataPanel() {
    const {table} = useParams() as { table: string };
    const {loading, error, data} = useSchema();

    if (loading) return <div>Loading...</div>;
    if (error) return <div>Error: {error.message}</div>;
    if (!table) return <div>Table missing</div>;

    return <DataDataTable table={getTable(data, table)}/>;
}