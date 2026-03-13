import { useQuery } from "@tanstack/react-query";
import { ReactElement, useMemo } from "react";
import { useForm } from "@tanstack/react-form";
import { useSchema } from "./hooks/useSchema";
import { Link, useParams, useNavigate } from "./hooks/usePath";
import { Schema, Table, Column, Join } from "./types/schema";
import { TableRefValue, useTableRef } from "./hooks/useTableRef";
import { useFetcher } from "./common/fetcher";
import { useTableMutation } from "./hooks/useTableMutation";
import {
    Dialog,
    DialogContent,
    DialogHeader,
    DialogTitle,
    DialogDescription,
    DialogFooter,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Checkbox } from "@/components/ui/checkbox";
import {
    Select,
    SelectContent,
    SelectItem,
    SelectTrigger,
    SelectValue,
} from "@/components/ui/select";
import { Label } from "@/components/ui/label";
import { Button } from "@/components/ui/button";
import { LoaderCircle } from "lucide-react";

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

    const defaultValues = useMemo(() => {
        const values: Record<string, unknown> = {};
        for (const { column: c } of editColumns) {
            if (dateTypes.some(t => t === c.paramType)) {
                const dateValue = (value[c.name] as string)?.split("T")?.[0];
                values[c.name] = dateValue === '0001-01-01' ? '' : (dateValue ?? '');
            } else if (booleanTypes.some(t => t === c.paramType)) {
                values[c.name] = !!value[c.name];
            } else {
                values[c.name] = value[c.name] ?? '';
            }
        }
        return values;
    }, [value, editColumns, isInsert]);

    const form = useForm({
        defaultValues,
        onSubmit: async ({ value: formValues }) => {
            const mutate = isInsert ? mutation.insert : mutation.update;
            await mutate({ ...formValues });
            navigate('../..');
        },
    });

    if (isLoading && !isInsert) return <div>Loading...</div>;
    if (error) return <div>Error: {(error as Error).message}</div>;

    const labelIdValue = (value?.[labelColumn] as string | number | undefined) ?? editid;

    return (
        <Dialog open onOpenChange={(open) => { if (!open) navigate('../..'); }}>
            <DialogContent className="sm:max-w-lg">
                <DialogHeader>
                    <DialogTitle>{label}:{labelIdValue}</DialogTitle>
                    <DialogDescription className="sr-only">
                        {isInsert ? "Create" : "Edit"} {label} record
                    </DialogDescription>
                </DialogHeader>
                {mutation.error && (
                    <p className="text-sm text-destructive">{mutation.error.message}</p>
                )}
                <form
                    onSubmit={(e) => {
                        e.preventDefault();
                        e.stopPropagation();
                        form.handleSubmit();
                    }}
                    className="grid gap-4"
                >
                    {editColumns.map(({ column: ec, join }: ColumnJoin) => (
                        <EditField
                            key={ec.name}
                            column={ec}
                            join={join}
                            form={form}
                            schema={schema}
                        />
                    ))}
                    <DialogFooter>
                        <Button variant="outline" asChild>
                            <Link to="../..">Cancel</Link>
                        </Button>
                        <Button type="submit" disabled={mutation.isPending}>
                            {mutation.isPending && (
                                <LoaderCircle className="animate-spin" />
                            )}
                            Save
                        </Button>
                    </DialogFooter>
                </form>
            </DialogContent>
        </Dialog>
    );
}

export function DataEdit(): ReactElement {
    const { table, editid } = useParams<DataEditRouteParams>();
    const schema = useSchema();

    if (!table) return <div>Table missing</div>;
    if (schema.loading) return <div>Loading...</div>;
    if (schema.error) return <div>Error: {schema.error.message}</div>;

    return <DataEditDetail table={table} schema={schema} editid={editid ?? ''} />
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
type AnyFieldApi = any;

interface EditFieldProps {
    column: Column;
    join?: Join;
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    form: any;
    schema: Schema;
}

function EditField({ column, join, form, schema }: EditFieldProps) {
    const name = column.name;
    const isRequired = !column.isNullable;
    const isBoolean = booleanTypes.some(t => t === column.paramType);
    const isDate = dateTypes.some(t => t === column.paramType);

    if (join) {
        return <ParentField column={column} join={join} form={form} schema={schema} isRequired={isRequired} />;
    }

    if (isBoolean) {
        return (
            <form.Field
                name={name}
                children={(field: AnyFieldApi) => (
                    <div className="flex items-center gap-3">
                        <Checkbox
                            id={name}
                            checked={!!field.state.value}
                            onCheckedChange={(checked) => field.handleChange(!!checked)}
                            onBlur={field.handleBlur}
                            aria-invalid={field.state.meta.errors.length > 0}
                        />
                        <Label htmlFor={name}>{column.label}</Label>
                        <FieldErrors errors={field.state.meta.errors} />
                    </div>
                )}
            />
        );
    }

    return (
        <form.Field
            name={name}
            validators={isRequired ? {
                onSubmit: ({ value }: { value: unknown }) => {
                    if (value === undefined || value === null || value === '') {
                        return `${column.label} is required`;
                    }
                    return undefined;
                },
            } : undefined}
            children={(field: AnyFieldApi) => (
                <div className="grid gap-2">
                    <Label htmlFor={name}>{column.label}</Label>
                    <Input
                        id={name}
                        type={isDate ? "date" : numericTypes.some(t => t === column.paramType) ? "number" : "text"}
                        value={(field.state.value as string | number) ?? ''}
                        onChange={(e) => field.handleChange(e.target.value)}
                        onBlur={field.handleBlur}
                        required={isRequired}
                        aria-invalid={field.state.meta.errors.length > 0}
                    />
                    <FieldErrors errors={field.state.meta.errors} />
                </div>
            )}
        />
    );
}

interface ParentFieldProps {
    column: Column;
    join: Join;
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    form: any;
    schema: Schema;
    isRequired: boolean;
}

function ParentField({ column, join, form, schema, isRequired }: ParentFieldProps) {
    const name = column.name;
    const parentData = useTableRef(schema, join.destinationTable, join.destinationColumnNames[0]);

    return (
        <form.Field
            name={name}
            validators={isRequired ? {
                onSubmit: ({ value }: { value: unknown }) => {
                    if (value === undefined || value === null || value === '') {
                        return `${column.label} is required`;
                    }
                    return undefined;
                },
            } : undefined}
            children={(field: AnyFieldApi) => (
                <div className="grid gap-2">
                    <Label htmlFor={name}>{column.label}</Label>
                    <Select
                        value={String(field.state.value ?? '')}
                        onValueChange={(val) => field.handleChange(val)}
                    >
                        <SelectTrigger id={name} className="w-full" aria-invalid={field.state.meta.errors.length > 0}>
                            <SelectValue placeholder={`Select ${column.label}`} />
                        </SelectTrigger>
                        <SelectContent>
                            {parentData.data.map((row: TableRefValue) => (
                                <SelectItem key={row.key} value={String(row.key)}>
                                    {row.label}
                                </SelectItem>
                            ))}
                        </SelectContent>
                    </Select>
                    <FieldErrors errors={field.state.meta.errors} />
                </div>
            )}
        />
    );
}

function FieldErrors({ errors }: { errors: unknown[] }) {
    if (errors.length === 0) return null;
    return (
        <>
            {errors.map((error, i) => (
                <p key={i} className="text-sm text-destructive">
                    {String(error)}
                </p>
            ))}
        </>
    );
}
