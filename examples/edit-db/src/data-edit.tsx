import { gql, useQuery } from "@apollo/client";
import React, { ReactElement } from "react";
import { useSchema } from "./hooks/useData";
import { Link, useParams } from "./hooks/usePath";

function getTable(data: any[], tableName: string) {
    const table = data.find((x: { name: string | undefined; }) => x.name == tableName);
    return table;
}

function DataEditDetail({ table, schema, editid }: { table: any, schema: any, editid: number }) {
    const dataTable = getTable(schema, table);
    const editColumns = dataTable.columns.filter((c: any) => (c?.type?.kind !== "OBJECT" && c?.type?.kind !== "LIST"));

    const { loading, error, data } = useQuery(
        gql`query GetSingle($id: Int){ ${dataTable.name}(filter: {id: { _eq: $id}}) { data { ${editColumns.map((c:any) => c.name).join(" ") } }}}`,
        { variables: { id: +editid } }
    );

    if (loading) return <div>Loading...</div>;
    if (error) return <div>Error: {error.message}</div>;
    const detail = data?.[dataTable.name]?.data?.at(0);

    return <section><form>
        <h3>{table}:{editid}</h3>
        <ul>
            {editColumns.map((ec: any) => <li key={ec.name}>
                <label>{ec.name}</label>
                <input type="text" defaultValue={detail?.[ec.name]} />
            </li>)}
        </ul>
        <button type="submit">edit</button>
        <Link to="../..">Cancel</Link>
    </form>
    </section>;

}

export function DataEdit(): ReactElement {
    console.log("It's me.")
    const { table, editid } = useParams();
    const { loading, error, data } = useSchema();

    if (!table) return <div>Table missing</div>;
    if (loading) return <div>Loading...</div>;
    if (error) return <div>Error: {error.message}</div>;

    return <DataEditDetail table={table} schema={data} editid={editid} />
}