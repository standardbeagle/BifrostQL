import { useQuery } from "@tanstack/react-query";
import { FormEvent, ReactElement, useEffect, useMemo, useRef } from "react";
import { useSchema } from "./hooks/useSchema";
import { Link, useParams, useNavigate } from "./hooks/usePath";
import './data-edit.scss';
import { Schema, Table, Column, Join } from "./types/schema";
import { TableRefValue, useTableRef } from "./hooks/useTableRef";
import { useFetcher } from "./common/fetcher";
import { useTableMutation } from "./hooks/useTableMutation";

const booleanTypes = ["Boolean", "Boolean!"];
const dateTypes = ["DateTime", "DateTime!"];
const numericTypes = ["Int", "Int!", "Float", "Float!"];

interface DataEditRouteParams {
    table?: string;
    editid?: string;
    [key: string]: string | undefined;
}

interface ColumnJoin {
    column: Column;
    join?: Join;
}

type DetailRecord = Record<string, string | number | boolean | undefined>;

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
    const fetcher = useFetcher();
    const isInsert = editid === undefined || editid === '';
    const [dataTable, editColumns, idColumns] = useTable(schema, table);
    const labelColumn = dataTable.labelColumn;
    const label = dataTable.label;

    const dialogRef = useRef<HTMLDialogElement>(null);
    const detailRef = useRef<DetailRecord>({});

    const mutation = useTableMutation(dataTable, editColumns, idColumns, editid);

    const pkName = idColumns[0]?.name ?? dataTable.primaryKeys?.[0] ?? "id";
    const pkColumn = idColumns[0] ?? dataTable.columns.find((c: Column) => c.name === pkName);
    const pkParamType = pkColumn?.paramType?.replace("!", "") ?? "Int";
    const isNumericPk = numericTypes.includes(pkParamType);

    const queryStr = useMemo(() =>
        `query GetSingleEdit_${dataTable.name}($id: ${pkParamType}){
            value: ${dataTable.name}(filter: {${pkName}: { _eq: $id}}) {
                data { ${editColumns.map(({ column: c }) => c.name).join(" ")} }
            }
        }`,
        [dataTable, editColumns, pkParamType, pkName]
    );

    const { isLoading, error, data } = useQuery({
        queryKey: ['editRecord', dataTable.name, editid],
        queryFn: () => fetcher.query<{ value: { data: Record<string, unknown>[] } }>(queryStr, { id: isNumericPk ? +editid : editid }),
        enabled: !!dataTable && !isInsert,
    });

    const value = useMemo(() => data?.value?.data?.at(0) ?? {}, [data]);

    // Initialize detailRef from loaded data
    useEffect(() => {
        detailRef.current = Object.fromEntries(editColumns.map(({ column: c }: ColumnJoin) => {
            if (dateTypes.some(t => t === c.paramType)) {
                const dateValue = (value[c.name] as string)?.split("T")?.[0];
                return [c.name, dateValue === '0001-01-01' ? '' : dateValue];
            }
            return [c.name, value[c.name] as string | number | boolean | undefined];
        }));
    }, [value, editColumns]);

    useEffect(() => {
        const dialog = dialogRef.current;
        dialog?.showModal();
        return () => dialog?.close();
    }, []);

    if (isLoading && !isInsert) return <div>Loading...</div>;
    if (error) return <div>Error: {(error as Error).message}</div>;
    if (mutation.error) return <div>Error: {mutation.error.message}</div>;

    const onSubmit = (_event: FormEvent<HTMLFormElement>) => {
        const mutate = isInsert ? mutation.insert : mutation.update;
        mutate({ ...detailRef.current }).then(() => {
            navigate('../..');
        });
    }

    const labelIdValue = (value?.[labelColumn] as string | number | undefined) ?? editid;

    const editFields: EditFieldDef[] = editColumns.map(({ column: ec, join }: ColumnJoin) => ({
        paramType: ec.paramType,
        isReadOnly: ec.isReadOnly,
        name: ec.name,
        required: ec.isNullable ? {} : { required: true },
        value: detailRef.current[ec.name] ?? (isInsert ? undefined : value[ec.name] as string | number | boolean | undefined),
        join
    }));

    return <dialog className="editdb-dialog-edit" ref={dialogRef}>
        <form method="dialog" onSubmit={onSubmit}>
            {idColumns.map((c: Column) =>
                <input key={c.name} type="hidden" name={c.name} defaultValue={editid} />
            )}
            <h3 className="editdb-dialog-edit__heading">{label}:{labelIdValue}</h3>
            <EditFields fields={editFields} detailRef={detailRef} />
            <div className="button-row">
                <Link className="editdb-dialog-edit__cancel" to="../..">Cancel</Link>
                <button
                    type="submit"
                    className={`editdb-dialog-edit__submit ${mutation.isPending ? 'editdb-dialog-edit__submit--loading' : ''}`}
                    disabled={mutation.isPending}
                >Save</button>
            </div>
        </form>
    </dialog>;
}

export function DataEdit(): ReactElement {
    const { table, editid } = useParams<DataEditRouteParams>();
    const schema = useSchema();

    if (!table) return <div>Table missing</div>;
    if (schema.loading) return <div>Loading...</div>;
    if (schema.error) return <div>Error: {schema.error.message}</div>;

    return <DataEditDetail table={table} schema={schema} editid={editid ?? ''} />
}

function EditFields({ fields, detailRef }: { fields: EditFieldDef[], detailRef: React.MutableRefObject<DetailRecord> }): JSX.Element {
    return (
        <ul className="editdb-dialog-edit__input-list">
            {fields.map((field: EditFieldDef) => <EditField key={field.name} field={field} detailRef={detailRef} />)}
        </ul>
    );
}

function EditField({ field, detailRef }: { field: EditFieldDef, detailRef: React.MutableRefObject<DetailRecord> }): JSX.Element {
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
            <InputComponent field={field} detailRef={detailRef} />
        </li>
    );
}

function BooleanInput({ field, detailRef }: { field: EditFieldDef, detailRef: React.MutableRefObject<DetailRecord> }): JSX.Element {
    return (
        <input
            type="checkbox"
            defaultChecked={field.value as boolean}
            onChange={event => { detailRef.current[field.name] = event.target.checked; }}
        />
    );
}

function DefaultInput({ field, detailRef }: { field: EditFieldDef, detailRef: React.MutableRefObject<DetailRecord> }): JSX.Element {
    return (
        <input
            type="text"
            defaultValue={field.value as string}
            {...field.required}
            onChange={event => { detailRef.current[field.name] = event.target.value; }}
        />
    );
}

function DateTimeInput({ field, detailRef }: { field: EditFieldDef, detailRef: React.MutableRefObject<DetailRecord> }): JSX.Element {
    return (
        <input
            type="date"
            defaultValue={field.value as string}
            {...field.required}
            onChange={event => { detailRef.current[field.name] = event.target.value; }}
        />
    );
}

function ParentInput({ field, detailRef }: { field: EditFieldDef, detailRef: React.MutableRefObject<DetailRecord> }): JSX.Element {
    const schema = useSchema();
    const parentData = useTableRef(schema, field.join!.destinationTable, field.join!.destinationColumnNames[0]);
    return (<select
        defaultValue={field.value as string}
        onChange={event => { detailRef.current[field.name] = event.target.value; }}
    >
        {parentData.data.map((row: TableRefValue) => <option key={row.key} value={row.key}>{row.label}</option>)}
    </select>);
}

interface EditFieldDef {
    paramType: string;
    name: string;
    isReadOnly: boolean;
    required: { required?: boolean };
    value: string | number | boolean | undefined;
    join?: Join;
}
