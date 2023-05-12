import React from 'react'
import DataTable from 'react-data-table-component'
import { Link } from './hooks/usePath';
import { useSchema } from './hooks/useData';

const columns = [
    {
        name: "Table",
        cell: (row: any) => <Link to={`/${row.name}`} className="plain-link">{row.label}</Link>,
    }
];

export function TableList() {
    const {loading, error, data} = useSchema();
    if (loading) return <div>Loading...</div>;
    if (error) return <div>Error: {error.message}</div>;

    return <div>
        <DataTable columns={columns} data={data}/>
    </div>
}