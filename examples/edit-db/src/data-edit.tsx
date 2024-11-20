import { gql, useMutation, useQuery } from "@apollo/client";
import { ReactElement, useEffect, useMemo, useRef } from "react";
import { useSchema } from "./hooks/useSchema";
import { Link, useParams, useNavigate } from "./hooks/usePath";
import './data-edit.scss';
import { Schema, Table, Column, Join } from "./types/schema";
import { TableRefValue, useTableRef } from "./hooks/useTableRef";

const numericTypes = ["Int", "Int!", "Float", "Float!"];
const booleanTypes = ["Boolean", "Boolean!"];
const dateTypes = ["DateTime", "DateTime!"];

interface ColumnJoin {
    column: Column;
    join?: Join;
}

function useTable(schema: Schema, tableName: string) {
    return useMemo(() => {
        const table = schema?.data?.find((x: { graphQlName: string | undefined; }) => x.graphQlName == tableName)!;
        const editColumns = table.columns.filter((c: Column) => !c.isReadOnly && !c.isIdentity);
        const editColumnsJoin = editColumns.map((c: Column) => ({ column: c, join: table.singleJoins.find((j: Join) => j.sourceColumnNames[0] === c.graphQlName) }));
        return [
            table,
            editColumnsJoin,
            table.columns.filter((c: Column) => c.isIdentity),
        ] as [Table, ColumnJoin[], Column[]];
    }, [schema, tableName]);
}

function DataEditDetail({ table, schema, editid }: { table: string, schema: Schema, editid: string }) {
    const navigate = useNavigate();
    const isInsert = editid === undefined;
    const [dataTable, editColumns, idColumns] = useTable(schema, table);
    const labelColumn = dataTable.labelColumn;
    const label = dataTable.label;

    const dialogRef = useRef<HTMLDialogElement>(null);
    const getSingleQuery = useGetSingleQuery(dataTable, editColumns);
    const updateMutation = useUpdateMutation(dataTable);
    const insertMutation = useInsertMutation(dataTable);

    const { loading, error, data } = useQuery(
        getSingleQuery,
        { skip: !dataTable, variables: { id: +editid }, fetchPolicy: "network-only" }
    );

    const [mutate, mutateState] = useMutation<any>(
        updateMutation,
        {
            refetchQueries: [`Get${dataTable.name}`]
        }
    );

    const [insertMutate, insertState] = useMutation<any>(
        insertMutation,
        {
            refetchQueries: [`Get${dataTable.name}`]
        }
    );
    useEffect(() => {
        dialogRef?.current?.showModal();
        return () => dialogRef?.current?.close();
    });

    if (loading || !dataTable) return <div>Loading...</div>;
    if (error) return <div>Error: {error.message}</div>;
    if (mutateState.error) return <div>Error: {mutateState.error.message}</div>;
    if (insertState.error) return <div>Error: {insertState.error.message}</div>;

    const value = (data?.value?.data?.at(0) ?? {});

    const detail = Object.fromEntries(editColumns.map(({ column: c }: ColumnJoin) => {
        if (dateTypes.some(t => t === c.paramType)) {
            const dateValue = value[c.name]?.split("T")?.[0];
            return [c.name, dateValue === '0001-01-01' ? '' : dateValue];
        }
        return [c.name, value[c.name]];
    }));

    const onSubmit = (event: any) => {
        for (const { column: col } of editColumns) {
            if (numericTypes.some(t => t === col.paramType)) {
                detail[col.name] = +detail[col.name];
            }
            if (booleanTypes.some(t => t === col.paramType)) {
                detail[col.name] = !!detail[col.name];
            }
        }
        for (const col of idColumns) {
            detail[col.name] = col.paramType.startsWith("Int") ? +editid : editid;
        }
        if (isInsert) {
            insertMutate({ variables: { detail } }).then(() => {
                navigate('../..');
            });
        } else {
            mutate({ variables: { detail } })
                .then(() => {
                    navigate('../..');
                });
        }
    }


    const labelIdValue = value?.[labelColumn] ?? editid;

    const editFields: EditFieldDef[] = editColumns.map(({ column: ec, join }: ColumnJoin) => ({
        paramType: ec.paramType,
        isReadOnly: ec.isReadOnly,
        name: ec.name,
        required: ec.isNullable ? {} : { required: true },
        value: detail[ec.name],
        join
    }));
    console.log({ editColumns, editFields, detail, dataTable, editid, isInsert });

    return <dialog className="editdb-dialog-edit" ref={dialogRef}>
        <form method="dialog" onSubmit={onSubmit}>
            {idColumns.map((c: Column) =>
                <input key={c.name} type="hidden" name={c.name} defaultValue={editid} />
            )}
            <h3 className="editdb-dialog-edit__heading">{label}:{labelIdValue}</h3>
            <EditFields fields={editFields} detail={detail} />
            <div className="button-row">
                <Link className="editdb-dialog-edit__cancel" to="../..">Cancel</Link>
                <button
                    type="submit"
                    className={`editdb-dialog-edit__submit ${(mutateState.loading || insertState.loading) ? 'editdb-dialog-edit__submit--loading' : ''}`}
                    disabled={mutateState.loading || insertState.loading}
                >Save</button>
            </div>
        </form>
    </dialog>;
}

export function DataEdit(): ReactElement {
    const { table, editid } = useParams();
    const schema = useSchema();

    if (!table) return <div>Table missing</div>;
    if (schema.loading) return <div>Loading...</div>;
    if (schema.error) return <div>Error: {schema.error.message}</div>;

    return <DataEditDetail table={table} schema={schema} editid={editid} />
}

function EditFields({ fields, detail }: { fields: EditFieldDef[], detail: any }): JSX.Element {
    return (
        <ul className="editdb-dialog-edit__input-list">
            {fields.map((field: any) => <EditField key={field.name} field={field} detail={detail} />)}
        </ul>
    );
}

function EditField({ field, detail }: { field: EditFieldDef, detail: any }): JSX.Element {
    let InputComponent = DefaultInput;

    if (field.join) {
        InputComponent = ParentInput;
    } else if (booleanTypes.some(t => t === field.paramType)) {
        InputComponent = BooleanInput;
    } else if (dateTypes.some(t => t === field.paramType)) {
        InputComponent = DateTimeInput;
    }

    return (
        <li className="editdb-dialog-edit__input-item">
            <label>{field.name}</label>
            <InputComponent field={field} detail={detail} />
        </li>
    );
}
function BooleanInput({ field, detail }: { field: EditFieldDef, detail: any }): JSX.Element {
    return (
        <input
            type="checkbox"
            defaultChecked={field.value}
            onChange={event => { detail[field.name] = event.target.checked; }}
        />
    );
}

function DefaultInput({ field, detail }: { field: EditFieldDef, detail: any }): JSX.Element {
    return (
        <input
            type="text"
            defaultValue={field.value}
            {...field.required}
            onChange={event => { detail[field.name] = event.target.value; }}
        />
    );
}

function DateTimeInput({ field, detail }: { field: EditFieldDef, detail: any }): JSX.Element {
    return (
        <input
            type="date"
            defaultValue={field.value}
            {...field.required}
            onChange={event => { detail[field.name] = event.target.value; }}
        />
    );
}

function ParentInput({ field, detail }: { field: EditFieldDef, detail: any }): JSX.Element {
    const schema = useSchema();
    console.log({ field, detail });
    const parentData = useTableRef(schema, field.join!.destinationTable, field.join!.destinationColumnNames[0]);
    return (<select
        onChange={event => { detail[field.name] = event.target.value; }}
    >
        {parentData.data.map((row: TableRefValue) => <option key={row.key} value={row.key} selected={row.key === field.value} >{row.label}</option>)}
    </select>);
}

interface EditFieldDef {
    paramType: string;
    name: string;
    isReadOnly: boolean;
    required: { required?: boolean };
    value: any;
    join?: Join;
}

const useGetSingleQuery = (dataTable: Table, editColumns: ColumnJoin[]) => useMemo(() => {
    return gql`query GetSingleEdit_${dataTable.name}($id: Int){ 
        value: ${dataTable.name}(filter: {id: { _eq: $id}}) { 
            data { ${editColumns.map(({ column: c }) => c.name).join(" ")} }
        }
    }`;
}, [dataTable, editColumns]);

const useUpdateMutation = (dataTable: Table) => useMemo(() => {
    return gql`mutation updateSingle($detail: Update_${dataTable.name}){ 
        ${dataTable.name}(update: $detail)
    }`;
}, [dataTable]);

const useInsertMutation = (dataTable: Table) => useMemo(() => {
    return gql`mutation insertSingle($detail: Insert_${dataTable.name}){ 
        ${dataTable.name}(insert: $detail)
    }`;
}, [dataTable]);
