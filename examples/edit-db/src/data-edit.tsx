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
    DialogDescription,
    DialogTitle,
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
import { LoaderCircle, Save, X, AlertCircle } from "lucide-react";
import { ContentEditor } from "@/components/content-editor";
import { isBinaryDbType, isLongTextDbType } from "@/lib/content-detect";

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
    }, [value, editColumns]);

    const form = useForm({
        defaultValues,
        onSubmit: async ({ value: formValues }) => {
            const mutate = isInsert ? mutation.insert : mutation.update;
            await mutate({ ...formValues });
            navigate('../..');
        },
    });

    if (isLoading && !isInsert) return null;
    if (error) return <div>Error: {(error as Error).message}</div>;

    const labelIdValue = (value?.[labelColumn] as string | number | undefined) ?? editid;

    // Separate fields into types for layout grouping
    const booleanFields = editColumns.filter(({ column: c }) => booleanTypes.some(t => t === c.paramType));
    const contentFields = editColumns.filter(({ column: c }) => isBinaryDbType(c.dbType) || isLongTextDbType(c.dbType));
    const standardFields = editColumns.filter(({ column: c }) =>
        !booleanTypes.some(t => t === c.paramType) &&
        !isBinaryDbType(c.dbType) &&
        !isLongTextDbType(c.dbType)
    );

    return (
        <Dialog open onOpenChange={(open) => { if (!open) navigate('../..'); }}>
            <DialogContent
                showCloseButton={false}
                className="sm:max-w-2xl max-h-[90vh] flex flex-col gap-0 p-0 overflow-hidden"
            >
                {/* Header */}
                <div className="flex items-center justify-between px-5 py-3 border-b border-border bg-muted/30 shrink-0">
                    <div className="min-w-0">
                        <DialogTitle className="text-sm font-semibold truncate">
                            {isInsert ? `New ${label}` : `${label} : ${labelIdValue}`}
                        </DialogTitle>
                        <DialogDescription className="sr-only">
                            {isInsert ? "Create" : "Edit"} {label} record
                        </DialogDescription>
                    </div>
                    <Button
                        variant="ghost"
                        size="icon-sm"
                        onClick={() => navigate('../..')}
                        aria-label="Close"
                        className="shrink-0 -mr-1"
                    >
                        <X className="size-4" />
                    </Button>
                </div>

                {/* Error banner */}
                {mutation.error && (
                    <div className="flex items-center gap-2 px-5 py-2 bg-destructive/10 border-b border-destructive/20 text-destructive text-sm shrink-0">
                        <AlertCircle className="size-3.5 shrink-0" />
                        <span className="truncate">{mutation.error.message}</span>
                    </div>
                )}

                {/* Scrollable form body */}
                <form
                    onSubmit={(e) => {
                        e.preventDefault();
                        e.stopPropagation();
                        form.handleSubmit();
                    }}
                    className="flex flex-col flex-1 min-h-0"
                >
                    <div className="flex-1 overflow-y-auto px-5 py-4">
                        {/* Standard fields — responsive two-column grid */}
                        {standardFields.length > 0 && (
                            <div className="grid grid-cols-1 sm:grid-cols-2 gap-x-4 gap-y-3">
                                {standardFields.map(({ column: ec, join }: ColumnJoin) => (
                                    <EditField
                                        key={ec.name}
                                        column={ec}
                                        join={join}
                                        form={form}
                                        schema={schema}
                                    />
                                ))}
                            </div>
                        )}

                        {/* Boolean fields — compact row */}
                        {booleanFields.length > 0 && (
                            <div className={standardFields.length > 0 ? "mt-4 pt-4 border-t border-border" : ""}>
                                <div className="flex flex-wrap gap-x-6 gap-y-2">
                                    {booleanFields.map(({ column: ec }: ColumnJoin) => (
                                        <BooleanField key={ec.name} column={ec} form={form} />
                                    ))}
                                </div>
                            </div>
                        )}

                        {/* Content/long-text fields — full width */}
                        {contentFields.length > 0 && (
                            <div className={standardFields.length > 0 || booleanFields.length > 0 ? "mt-4 pt-4 border-t border-border" : ""}>
                                <div className="grid gap-3">
                                    {contentFields.map(({ column: ec }: ColumnJoin) => (
                                        <ContentField key={ec.name} column={ec} form={form} />
                                    ))}
                                </div>
                            </div>
                        )}
                    </div>

                    {/* Footer */}
                    <div className="flex items-center justify-end gap-2 px-5 py-3 border-t border-border bg-muted/30 shrink-0">
                        <Button variant="outline" size="sm" asChild>
                            <Link to="../..">Cancel</Link>
                        </Button>
                        <Button type="submit" size="sm" disabled={mutation.isPending}>
                            {mutation.isPending ? (
                                <LoaderCircle className="size-3.5 animate-spin" />
                            ) : (
                                <Save className="size-3.5" />
                            )}
                            {isInsert ? 'Create' : 'Save'}
                        </Button>
                    </div>
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
    schema?: Schema;
}

function EditField({ column, join, form, schema }: EditFieldProps) {
    const name = column.name;
    const isRequired = !column.isNullable;
    const isDate = dateTypes.some(t => t === column.paramType);

    if (join && schema) {
        return <ParentField column={column} join={join} form={form} schema={schema} isRequired={isRequired} />;
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
                <div className="grid gap-1">
                    <Label htmlFor={name} className="text-xs text-muted-foreground">
                        {column.label}
                        {isRequired && <span className="text-destructive ml-0.5">*</span>}
                    </Label>
                    <Input
                        id={name}
                        type={isDate ? "date" : numericTypes.some(t => t === column.paramType) ? "number" : "text"}
                        value={(field.state.value as string | number) ?? ''}
                        onChange={(e) => field.handleChange(e.target.value)}
                        onBlur={field.handleBlur}
                        required={isRequired}
                        aria-invalid={field.state.meta.errors.length > 0}
                        className="h-8 text-sm"
                    />
                    <FieldErrors errors={field.state.meta.errors} />
                </div>
            )}
        />
    );
}

function BooleanField({ column, form }: { column: Column; form: AnyFieldApi }) {
    const name = column.name;
    return (
        <form.Field
            name={name}
            children={(field: AnyFieldApi) => (
                <label htmlFor={name} className="flex items-center gap-2 cursor-pointer select-none">
                    <Checkbox
                        id={name}
                        checked={!!field.state.value}
                        onCheckedChange={(checked) => field.handleChange(!!checked)}
                        onBlur={field.handleBlur}
                    />
                    <span className="text-sm">{column.label}</span>
                </label>
            )}
        />
    );
}

function ContentField({ column, form }: { column: Column; form: AnyFieldApi }) {
    const name = column.name;
    const isRequired = !column.isNullable;
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
                <div className="grid gap-1">
                    <Label htmlFor={name} className="text-xs text-muted-foreground">
                        {column.label}
                        {isRequired && <span className="text-destructive ml-0.5">*</span>}
                    </Label>
                    <ContentEditor
                        name={name}
                        label={column.label}
                        value={String(field.state.value ?? '')}
                        dbType={column.dbType}
                        onChange={(val) => field.handleChange(val)}
                        onBlur={field.handleBlur}
                        required={isRequired}
                        invalid={field.state.meta.errors.length > 0}
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
                <div className="grid gap-1">
                    <Label htmlFor={name} className="text-xs text-muted-foreground">
                        {column.label}
                        {isRequired && <span className="text-destructive ml-0.5">*</span>}
                    </Label>
                    <Select
                        value={String(field.state.value ?? '')}
                        onValueChange={(val) => field.handleChange(val)}
                    >
                        <SelectTrigger id={name} className="w-full h-8 text-sm" aria-invalid={field.state.meta.errors.length > 0}>
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
                <p key={i} className="text-xs text-destructive">
                    {String(error)}
                </p>
            ))}
        </>
    );
}
