import { gql, useMutation, useQuery } from "@apollo/client";
import React, { ReactElement, useEffect, useRef } from "react";
import { useSchema } from "./hooks/useData";
import { Link, useParams, useNavigate } from "./hooks/usePath";
import './data-edit.scss';

function getTable(data: any[], tableName: string) {
    const table = data.find((x: { name: string | undefined; }) => x.name == tableName);
    return table;
}

function DataEditDetail({ table, schema, editid }: { table: any, schema: any, editid: number }) {
    const isInsert = editid === undefined;
    ///console.log({ editid, isInsert });
    const dataTable = getTable(schema, table);
    const navigate = useNavigate();
    const editColumns = dataTable.columns
        .filter((c: any) => (c?.type?.kind !== "OBJECT" && c?.type?.kind !== "LIST"))
        .filter((c: any) => !isInsert || c?.name !== "id")
        .filter((c: any) => !(c?.name.endsWith("On") || c?.name.endsWith("By")));
    //console.log(editColumns);

    const dialogRef = useRef<HTMLDialogElement>(null);
    const { loading, error, data } = useQuery(
        gql`query GetSingle($id: Int){ ${dataTable.name}(filter: {id: { _eq: $id}}) { data { ${editColumns.map((c: any) => c.name).join(" ")} }}}`,
        { variables: { id: +editid } }
    );

    //console.log('dt', dataTable)
    const [mutate, mutateState] = useMutation<any>(
        gql`mutation updateSingle($detail: Update_${dataTable.name}){ ${dataTable.name}(update: $detail)}`,
        {
            refetchQueries: [`Get${dataTable.name}`]
        }
    );
    const [insertMutate, insertState] = useMutation<any>(
        gql`mutation insertSingle($detail: Insert_${dataTable.name}){ ${dataTable.name}(insert: $detail)}`,
        {
            refetchQueries: [`Get${dataTable.name}`]
        }
    );

    useEffect(() => {
        dialogRef?.current?.showModal();
        return () => dialogRef?.current?.close();
    });

    if (loading) return <div>Loading...</div>;
    if (error) return <div>Error: {error.message}</div>;
    const value = data?.[dataTable.name]?.data?.at(0) ?? {};
    const detail = Object.fromEntries(editColumns.map((c: any) => {
        if (c.paramType === "DateTime" || c.paramType === "DateTime!") {
            return [c.name, value[c.name]?.split("T")[0]];
        }
        return [c.name, value[c.name]]; 
    }));

    const onSubmit = (event: any) => {
        //console.log({event, detail});
        for(const col of editColumns) {
            //console.log(col.name, col.paramType, detail[col.name]);
            if(col.paramType === "Int" || col.paramType === "Int!") {
                detail[col.name] = +detail[col.name];
            }
        }
        if (isInsert) {
            insertMutate({ variables: { detail } }).then(update => {
                //console.log(update);
                navigate('../..');
            });
        } else {
            mutate({ variables: { detail } })
            .then(update => {
                //console.log(update);
                navigate('../..');
            });
        }
    }

    return <dialog className="editdb-dialog-edit" ref={dialogRef}>
        <form method="dialog" onSubmit={onSubmit}>
            <h3 className="editdb-dialog-edit__heading">{table}:{editid}</h3>
            <ul className="editdb-dialog-edit__input-list">
                {editColumns.map((ec: any) => <li key={ec.name} className="editdb-dialog-edit__input-item">
                    <label>{ec.name}</label>
                    <input type={ec.paramType === "DateTime" ? "date" : "text"} defaultValue={detail?.[ec.name]} onChange={event => { detail[ec.name] = event.target.value; }} />
                </li>)}
            </ul>
            <Link className="editdb-dialog-edit__cancel" to="../..">Cancel</Link>
            <button type="submit" className="editdb-dialog-edit__submit">edit</button>
        </form>
    </dialog>;
}

export function DataEdit(): ReactElement {
    const { table, editid } = useParams();
    const { loading, error, data } = useSchema();

    if (!table) return <div>Table missing</div>;
    if (loading) return <div>Loading...</div>;
    if (error) return <div>Error: {error.message}</div>;

    return <DataEditDetail table={table} schema={data} editid={editid} />
}