import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { FormEvent, ReactElement, useEffect, useMemo, useRef } from "react";
import { useSchema } from "./hooks/useSchema";
import { Link, useParams, useNavigate } from "./hooks/usePath";
import './data-edit.scss';
import { Schema, Table, Column, Join } from "./types/schema";
import { TableRefValue, useTableRef } from "./hooks/useTableRef";
import { useFetcher } from "./common/fetcher";

const numericTypes = ["Int", "Int!", "Float", "Float!"];
const booleanTypes = ["Boolean", "Boolean!"];
const dateTypes = ["DateTime", "DateTime!"];

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
    const queryClient = useQueryClient();
    const isInsert = editid === undefined || editid === '';
    const [dataTable, editColumns, idColumns] = useTable(schema, table);
    const labelColumn = dataTable.labelColumn;
    const label = dataTable.label;

    const dialogRef = useRef<HTMLDialogElement>(null);
    const detailRef = useRef<DetailRecord>({});

    const queryStr = useMemo(() =>
        `query GetSingleEdit_${dataTable.name}($id: Int){
            value: ${dataTable.name}(filter: {id: { _eq: $id}}) {
                data { ${editColumns.map(({ column: c }) => c.name).join(" ")} }
            }
        }`,
        [dataTable, editColumns]
    );

    const updateQueryStr = useMemo(() =>
        `mutation updateSingle($detail: Update_${dataTable.name}){
            ${dataTable.name}(update: $detail)
        }`,
        [dataTable]
    );

    const insertQueryStr = useMemo(() =>
        `mutation insertSingle($detail: Insert_${dataTable.name}){
            ${dataTable.name}(insert: $detail)
        }`,
        [dataTable]
    );

    const { isLoading, error, data } = useQuery({
        queryKey: ['editRecord', dataTable.name, editid],
        queryFn: () => fetcher.query<{ value: { data: Record<string, unknown>[] } }>(queryStr, { id: +editid }),
        enabled: !!dataTable && !isInsert,
    });

    const updateMutation = useMutation({
        mutationFn: (detail: DetailRecord) => fetcher.query(updateQueryStr, { detail }),
        onSuccess: () => queryClient.invalidateQueries({ queryKey: ['tableData', dataTable.name] }),
    });

    const insertMutation = useMutation({
        mutationFn: (detail: DetailRecord) => fetcher.query(insertQueryStr, { detail }),
        onSuccess: () => queryClient.invalidateQueries({ queryKey: ['tableData', dataTable.name] }),
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
    if (updateMutation.error) return <div>Error: {(updateMutation.error as Error).message}</div>;
    if (insertMutation.error) return <div>Error: {(insertMutation.error as Error).message}</div>;

    const isMutating = updateMutation.isPending || insertMutation.isPending;

    const onSubmit = (_event: FormEvent<HTMLFormElement>) => {
        const detail = { ...detailRef.current };
        for (const { column: col } of editColumns) {
            if (numericTypes.some(t => t === col.paramType)) {
                const val = detail[col.name];
                detail[col.name] = val !== undefined ? +val : undefined;
            }
            if (booleanTypes.some(t => t === col.paramType)) {
                detail[col.name] = !!detail[col.name];
            }
        }
        for (const col of idColumns) {
            detail[col.name] = col.paramType.startsWith("Int") ? +editid : editid;
        }
        const mutation = isInsert ? insertMutation : updateMutation;
        mutation.mutateAsync(detail).then(() => {
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
                    className={`editdb-dialog-edit__submit ${isMutating ? 'editdb-dialog-edit__submit--loading' : ''}`}
                    disabled={isMutating}
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
