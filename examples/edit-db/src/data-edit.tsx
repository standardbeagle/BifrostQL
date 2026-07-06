import { useQuery } from "@tanstack/react-query";
import { ReactElement, useMemo, useState } from "react";
import { useForm, useStore, AnyFieldApi, ReactFormExtendedApi } from "@tanstack/react-form";
import { useSchema } from "./hooks/useSchema";
import { useParams, useNavigate } from "./hooks/usePath";
import { Schema, Table, Column, Join } from "./types/schema";
import { TableRefValue, useTableRef } from "./hooks/useTableRef";
import { useCompositeTableRef } from "./hooks/useCompositeTableRef";
import { useFetcher } from "./common/fetcher";
import { useTableMutation } from "./hooks/useTableMutation";
import { parsePkRoute, buildPkEqFilter, encodeRouteParts } from "./lib/row-id";
import { validateFieldValue } from "./lib/field-validation";
import { isComposite } from "./lib/fk";
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
import { ConfirmDialog } from "@/components/confirm-dialog";
import { LoaderCircle, Save, X, AlertCircle } from "lucide-react";
import { ContentEditor } from "@/components/content-editor";
import { isBinaryDbType, isLongTextDbType, isJsonDbType } from "@/lib/content-detect";
import { resolveDisplayFormat } from "./lib/format-value";

const booleanTypes = ["Boolean", "Boolean!"];
const dateTypes = ["DateTime", "DateTime!"];
const numericTypes = ["Int", "Int!", "Float", "Float!"];

// Sentinel for the "(none)" option in FK/enum selects. Radix's Select rejects an
// empty-string item value, so we round-trip this and map it back to '' on change.
const NONE_VALUE = "__none__";

/** JSON columns arrive parsed (object/array) from the JSON scalar. */
function isJsonColumn(c: Column): boolean {
    return isJsonDbType(c.dbType) || c.paramType === 'JSON';
}

/** Columns edited through the ContentEditor (textarea) rather than a plain input. */
function isContentColumn(c: Column): boolean {
    return isBinaryDbType(c.dbType) || isLongTextDbType(c.dbType) || isJsonColumn(c);
}

/** True when a date-ish column carries a time-of-day component (datetime, not a bare date). */
function isDateTimeColumn(column: Column): boolean {
    return resolveDisplayFormat(column) === 'datetime';
}

/**
 * Normalizes a stored date/datetime string into the value an `<input type="date">`
 * or `<input type="datetime-local">` expects. For datetime columns the time-of-day is
 * preserved (fractional seconds and timezone offset are trimmed) so re-saving an
 * unrelated field does not overwrite the time with midnight. Returns '' for the
 * SQL "zero" date and unparseable input.
 */
function toDateInputValue(raw: string | undefined, withTime: boolean): string {
    if (!raw) return '';
    const m = raw.match(/^(\d{4}-\d{2}-\d{2})(?:[T ](\d{2}:\d{2}(?::\d{2})?))?/);
    if (!m) return '';
    if (m[1] === '0001-01-01') return '';
    if (!withTime) return m[1];
    if (!m[2]) return m[1]; // datetime column with no time part stored
    const time = m[2].length === 5 ? `${m[2]}:00` : m[2];
    return `${m[1]}T${time}`;
}

const EMPTY_PK_EQ_FILTER = { filterText: "", variables: {} as Record<string, unknown>, params: [] as string[] };

interface DataEditRouteParams {
    table?: string;
    editid?: string;
    [key: string]: string | undefined;
}

type FkRole = 'none' | 'anchor-single' | 'anchor-composite' | 'member-composite';

interface ColumnJoin {
    column: Column;
    join?: Join;
    /**
     * Where this column sits in its FK (if any):
     *   - none: not part of any FK
     *   - anchor-single: only source column of a single-column FK (legacy ParentField)
     *   - anchor-composite: first source column of a composite FK (drives the CompositeParentField)
     *   - member-composite: subsequent composite-FK column (form input is hidden; value driven by anchor)
     */
    fkRole: FkRole;
}

function useTable(schema: Schema, tableName: string) {
    return useMemo(() => {
        const table = schema?.data?.find((x: { graphQlName: string | undefined; }) => x.graphQlName == tableName)!;
        const editColumns = table.columns.filter((c: Column) => !c.isReadOnly && !c.isIdentity);
        const editColumnsJoin: ColumnJoin[] = editColumns.map((c: Column) => {
            const anchorJoin = table.singleJoins.find((j: Join) => j.sourceColumnNames[0] === c.graphQlName);
            if (anchorJoin) {
                return {
                    column: c,
                    join: anchorJoin,
                    fkRole: isComposite(anchorJoin) ? ('anchor-composite' as const) : ('anchor-single' as const),
                };
            }
            const memberJoin = table.singleJoins.find((j: Join) => (j.sourceColumnNames ?? []).includes(c.graphQlName));
            if (memberJoin) {
                return { column: c, join: memberJoin, fkRole: 'member-composite' as const };
            }
            return { column: c, fkRole: 'none' as const };
        });
        // idColumns is every primary-key column in declaration order. Composite PKs need them
        // all for the by-id filter and for the update mutation's WHERE clause. Previously this
        // was `c.isIdentity` which only captured auto-increment columns and silently dropped
        // the second half of a composite key.
        const byName = new Map(table.columns.map((c: Column) => [c.name, c] as const));
        const idColumns: Column[] = (table.primaryKeys ?? [])
            .map((pk) => byName.get(pk))
            .filter((c): c is Column => !!c);
        return [
            table,
            editColumnsJoin,
            idColumns,
        ] as [Table, ColumnJoin[], Column[]];
    }, [schema, tableName]);
}

function DataEditDetail({ table, schema, editid, onClose }: { table: string, schema: Schema, editid: string, onClose: () => void }) {
    const fetcher = useFetcher();
    const isInsert = editid === undefined || editid === '';
    const [dataTable, editColumns, idColumns] = useTable(schema, table);
    const labelColumn = dataTable.labelColumn;
    const label = dataTable.label;

    const mutation = useTableMutation(dataTable, editColumns, idColumns, editid);

    const pkEq = useMemo(() => {
        if (isInsert) return EMPTY_PK_EQ_FILTER;
        const parsed = parsePkRoute(editid, dataTable);
        if (!parsed) return EMPTY_PK_EQ_FILTER;
        return buildPkEqFilter(parsed, dataTable) ?? EMPTY_PK_EQ_FILTER;
    }, [isInsert, editid, dataTable]);

    const queryStr = useMemo(() => {
        const paramDecls = pkEq.params.length > 0 ? pkEq.params.join(", ") : "";
        return `query GetSingleEdit_${dataTable.name}(${paramDecls}){
            value: ${dataTable.name}(filter: ${pkEq.filterText || "{}"}) {
                data { ${editColumns.map(({ column: c }) => c.name).join(" ")} }
            }
        }`;
    }, [dataTable, editColumns, pkEq]);

    const { isLoading, error, data } = useQuery({
        queryKey: ['editRecord', dataTable.name, editid],
        queryFn: () => fetcher.query<{ value: { data: Record<string, unknown>[] } }>(queryStr, pkEq.variables),
        enabled: !!dataTable && !isInsert && pkEq.filterText !== "",
    });

    const value = useMemo(() => data?.value?.data?.at(0) ?? {}, [data]);

    const defaultValues = useMemo(() => {
        const values: Record<string, unknown> = {};
        for (const { column: c, fkRole } of editColumns) {
            const isFkOrEnum = fkRole !== 'none' || (c.enumValues !== undefined && c.enumValues.length > 0);
            if (dateTypes.some(t => t === c.paramType)) {
                values[c.name] = toDateInputValue(value[c.name] as string | undefined, isDateTimeColumn(c));
            } else if (booleanTypes.some(t => t === c.paramType)) {
                // Preserve NULL for nullable booleans so re-saving an unrelated
                // field doesn't silently coerce an unset boolean to false.
                const raw = value[c.name];
                values[c.name] = c.isNullable && (raw === null || raw === undefined)
                    ? null
                    : !!raw;
            } else if (isJsonColumn(c)) {
                // JSON columns arrive already parsed (object/array/number/string).
                // Always serialize — including string values — so the editor shows
                // canonical JSON (a stored JSON string "123" reads as "123", not the
                // bare 123), keeping the parse-back on save lossless.
                const raw = value[c.name];
                values[c.name] = raw === undefined || raw === null
                    ? ''
                    : JSON.stringify(raw, null, 2);
            } else if (isFkOrEnum) {
                // Keep NULL for an unset FK/enum so re-saving an untouched row
                // doesn't turn it into '' (a bogus value / FK violation).
                const raw = value[c.name];
                values[c.name] = raw === null || raw === undefined ? null : raw;
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
            onClose();
        },
    });

    // Guard against silently dropping unsaved edits: closing a dirty form
    // (Escape, overlay click, header X, Cancel) prompts for confirmation first.
    const isDirty = useStore(form.store, (s: { isDirty: boolean }) => s.isDirty);
    const [confirmDiscard, setConfirmDiscard] = useState(false);
    const requestClose = () => {
        if (isDirty) setConfirmDiscard(true);
        else onClose();
    };

    // Show the dialog shell with a spinner while the record loads, instead of
    // rendering nothing (which left the Edit click with no feedback).
    if (isLoading && !isInsert) {
        return (
            <Dialog open onOpenChange={(open) => { if (!open) onClose(); }}>
                <DialogContent showCloseButton={false} className="sm:max-w-2xl">
                    <DialogTitle className="sr-only">Loading record</DialogTitle>
                    <DialogDescription className="sr-only">Loading record for editing</DialogDescription>
                    <div className="flex items-center justify-center gap-2 py-10 text-sm text-muted-foreground">
                        <LoaderCircle className="size-4 animate-spin" />
                        Loading…
                    </div>
                </DialogContent>
            </Dialog>
        );
    }
    if (error) return <div>Error: {(error as Error).message}</div>;

    const labelIdValue = (value?.[labelColumn] as string | number | undefined) ?? editid;

    // Separate fields into types for layout grouping
    const booleanFields = editColumns.filter(({ column: c }) => booleanTypes.some(t => t === c.paramType));
    const contentFields = editColumns.filter(({ column: c }) => isContentColumn(c));
    const standardFields = editColumns.filter(({ column: c }) =>
        !booleanTypes.some(t => t === c.paramType) &&
        !isContentColumn(c)
    );

    return (
        <Dialog open onOpenChange={(open) => { if (!open) requestClose(); }}>
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
                        type="button"
                        variant="ghost"
                        size="icon-sm"
                        onClick={requestClose}
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
                                {standardFields.map(({ column: ec, join, fkRole }: ColumnJoin) => (
                                    <EditField
                                        key={ec.name}
                                        column={ec}
                                        join={join}
                                        fkRole={fkRole}
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
                        <Button type="button" variant="outline" size="sm" onClick={requestClose}>
                            Cancel
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
            <ConfirmDialog
                open={confirmDiscard}
                onOpenChange={setConfirmDiscard}
                title="Discard unsaved changes?"
                description={<p>You have unsaved changes. Closing now will discard them.</p>}
                confirmLabel="Discard"
                cancelLabel="Keep editing"
                variant="destructive"
                onConfirm={() => { setConfirmDiscard(false); onClose(); }}
            />
        </Dialog>
    );
}

/**
 * DataEdit component - Form interface for creating and editing database records.
 * 
 * Automatically generates form fields from table schema metadata, including
 * validation rules, foreign key dropdowns, and content editors for large fields.
 * 
 * @example
 * ```tsx
 * // Used within the router context
 * <Route path="/:table/edit/:editid" element={<DataEdit />} />
 * ```
 * 
 * @returns React element containing the edit form dialog
 */
export function DataEdit(): ReactElement {
    const { table, editid } = useParams<DataEditRouteParams>();
    const navigate = useNavigate();

    if (!table) return <div>Table missing</div>;

    // Route-driven edit (deep links, header "New"): close returns up the route.
    return <DataEditDialog table={table} editid={editid ?? ''} onClose={() => navigate('../..')} />;
}

/**
 * Prop-driven edit dialog. Use this to edit a record in place — from a grid at
 * any nesting depth — without changing the route, so the surrounding drill
 * context (parent → child → grandchild, side columns) is preserved. `table` is
 * the table's `graphQlName`; `editid` is the encoded PK route ('' to insert).
 */
export function DataEditDialog({ table, editid, onClose }: { table: string; editid: string; onClose: () => void }): ReactElement {
    const schema = useSchema();

    if (!table) return <div>Table missing</div>;
    if (schema.loading) return <div>Loading...</div>;
    if (schema.error) return <div>Error: {schema.error.message}</div>;

    return <DataEditDetail table={table} schema={schema} editid={editid} onClose={onClose} />;
}

/**
 * Type-erased form handle for field components that don't care about the
 * specific field-level validator generics — mirrors the library's own
 * `AnyFieldApi` pattern (see `@tanstack/form-core`), applied to the React
 * form object returned by `useForm`. Form values are always keyed by column
 * name (see `defaultValues` in `DataEditDetail`), so `TFormData` is pinned to
 * `Record<string, unknown>` — only the per-validator generics (which every
 * field component here leaves unset) are erased to `any`. Pinning `TFormData`
 * also avoids a TS variance quirk where `any` there breaks contravariant
 * method params (e.g. `pushFieldValue`) and drops `.useStore` from the
 * intersection type.
 */
// eslint-disable-next-line @typescript-eslint/no-explicit-any
type AnyReactFormApi = ReactFormExtendedApi<Record<string, unknown>, any, any, any, any, any, any, any, any, any, any, any>;

interface EditFieldProps {
    column: Column;
    join?: Join;
    fkRole?: FkRole;
    form: AnyReactFormApi;
    schema?: Schema;
}

function CharacterCounter({ current, max }: { current: number; max: number }) {
    const remaining = max - current;
    const isNearLimit = remaining <= 10 && remaining > 0;
    const isAtLimit = remaining <= 0;
    
    return (
        <div className="text-xs text-right mt-0.5" aria-live="polite">
            <span 
                className={
                    isAtLimit 
                        ? "text-destructive font-medium" 
                        : isNearLimit 
                            ? "text-amber-500" 
                            : "text-muted-foreground"
                }
            >
                {current}/{max}
            </span>
        </div>
    );
}

function RangeHint({ min, max, step }: { min?: number | null; max?: number | null; step?: number | null }) {
    if (min === undefined && max === undefined) return null;
    
    let hint = '';
    if (min !== undefined && min !== null && max !== undefined && max !== null) {
        hint = `Range: ${min} to ${max}`;
    } else if (min !== undefined && min !== null) {
        hint = `Min: ${min}`;
    } else if (max !== undefined && max !== null) {
        hint = `Max: ${max}`;
    }
    
    if (step !== undefined && step !== null && step !== 1) {
        hint += ` (step: ${step})`;
    }
    
    return (
        <div className="text-xs text-muted-foreground mt-0.5" aria-live="polite">
            {hint}
        </div>
    );
}

function EditField({ column, join, fkRole, form, schema }: EditFieldProps) {
    const name = column.name;
    const isRequired = !column.isNullable;

    // Composite-FK member columns are driven by the anchor's CompositeParentField — hide the input.
    if (fkRole === 'member-composite') return null;
    const isDate = dateTypes.some(t => t === column.paramType);
    const isNumeric = numericTypes.some(t => t === column.paramType);
    const showCharCounter = column.maxLength && column.maxLength > 0 && !isDate && !isNumeric;

    // Use enum values if available
    if (column.enumValues && column.enumValues.length > 0) {
        return (
            <EnumField
                column={column}
                form={form}
                isRequired={isRequired}
            />
        );
    }

    if (join && schema) {
        if (fkRole === 'anchor-composite') {
            return <CompositeParentField column={column} join={join} form={form} schema={schema} isRequired={isRequired} />;
        }
        return <ParentField column={column} join={join} form={form} schema={schema} isRequired={isRequired} />;
    }

    // Determine input type based on schema metadata or paramType. Datetime columns get a
    // datetime-local input so the time-of-day is editable and preserved on save; a bare
    // date column keeps a date input.
    const inputType = column.inputType
        || (isDate ? (isDateTimeColumn(column) ? "datetime-local" : "date") : isNumeric ? "number" : "text");

    // Build validators based on schema constraints. Shared with the server via
    // validateFieldValue so client and server enforce identical rules. Also runs
    // on blur so the user gets per-field feedback before hitting Save.
    const validators = {
        onSubmit: ({ value }: { value: unknown }) => validateFieldValue(column, value, isRequired),
        onBlur: ({ value }: { value: unknown }) => validateFieldValue(column, value, isRequired),
    };

    const errorId = `${name}-error`;

    return (
        <form.Field
            name={name}
            validators={validators}
            children={(field: AnyFieldApi) => {
                const hasError = field.state.meta.errors.length > 0;
                return (
                <div className="grid gap-1">
                    <Label htmlFor={name} className="text-xs text-muted-foreground">
                        {column.label}
                        {isRequired && <span className="text-destructive ml-0.5">*</span>}
                    </Label>
                    <Input
                        id={name}
                        type={inputType}
                        value={(field.state.value as string | number) ?? ''}
                        onChange={(e) => field.handleChange(e.target.value)}
                        onBlur={field.handleBlur}
                        required={isRequired}
                        maxLength={column.maxLength}
                        minLength={column.minLength}
                        min={column.min}
                        max={column.max}
                        step={column.step}
                        pattern={column.pattern}
                        title={column.patternMessage}
                        aria-invalid={hasError}
                        aria-describedby={hasError ? errorId : undefined}
                        className="h-8 text-sm"
                    />
                    {showCharCounter && (
                        <CharacterCounter
                            current={String(field.state.value ?? '').length}
                            max={column.maxLength!}
                        />
                    )}
                    {isNumeric && (column.min !== undefined || column.max !== undefined) && (
                        <RangeHint min={column.min} max={column.max} step={column.step} />
                    )}
                    <FieldErrors errors={field.state.meta.errors} id={errorId} />
                </div>
                );
            }}
        />
    );
}

interface EnumFieldProps {
    column: Column;
    form: AnyReactFormApi;
    isRequired: boolean;
}

function EnumField({ column, form, isRequired }: EnumFieldProps) {
    const name = column.name;
    const enumValues = column.enumValues || [];
    // Labels are positional, so a count mismatch would mislabel options. Trust
    // them only when they line up 1:1; otherwise show the raw values.
    const enumLabels =
        column.enumLabels && column.enumLabels.length === enumValues.length
            ? column.enumLabels
            : enumValues;

    const errorId = `${name}-error`;

    return (
        <form.Field
            name={name}
            validators={{
                onSubmit: ({ value }: { value: unknown }) => validateFieldValue(column, value, isRequired),
                onBlur: ({ value }: { value: unknown }) => validateFieldValue(column, value, isRequired),
            }}
            children={(field: AnyFieldApi) => {
                const hasError = field.state.meta.errors.length > 0;
                return (
                <div className="grid gap-1">
                    <Label htmlFor={name} className="text-xs text-muted-foreground">
                        {column.label}
                        {isRequired && <span className="text-destructive ml-0.5">*</span>}
                    </Label>
                    <Select
                        value={String(field.state.value ?? '')}
                        onValueChange={(val) => field.handleChange(val === NONE_VALUE ? null : val)}
                    >
                        <SelectTrigger id={name} className="w-full h-8 text-sm" aria-invalid={hasError} aria-describedby={hasError ? errorId : undefined}>
                            <SelectValue placeholder={`Select ${column.label}`} />
                        </SelectTrigger>
                        <SelectContent>
                            {!isRequired && (
                                <SelectItem value={NONE_VALUE} className="text-muted-foreground">(none)</SelectItem>
                            )}
                            {enumValues.map((value, index) => (
                                <SelectItem key={value} value={value}>
                                    {enumLabels[index] || value}
                                </SelectItem>
                            ))}
                        </SelectContent>
                    </Select>
                    <FieldErrors errors={field.state.meta.errors} id={errorId} />
                </div>
                );
            }}
        />
    );
}

const BOOL_TRUE = "true";
const BOOL_FALSE = "false";

function BooleanField({ column, form }: { column: Column; form: AnyReactFormApi }) {
    const name = column.name;

    // Nullable booleans get a tri-state control (Unset / Yes / No) so an unset
    // value stays NULL instead of collapsing to false on the next save.
    if (column.isNullable) {
        return (
            <form.Field
                name={name}
                children={(field: AnyFieldApi) => {
                    const v = field.state.value;
                    const current = v === null || v === undefined
                        ? NONE_VALUE
                        : v ? BOOL_TRUE : BOOL_FALSE;
                    return (
                        <div className="grid gap-1 min-w-[10rem]">
                            <Label htmlFor={name} className="text-xs text-muted-foreground">{column.label}</Label>
                            <Select
                                value={current}
                                onValueChange={(val) => field.handleChange(
                                    val === NONE_VALUE ? null : val === BOOL_TRUE,
                                )}
                            >
                                <SelectTrigger id={name} className="w-full h-8 text-sm">
                                    <SelectValue />
                                </SelectTrigger>
                                <SelectContent>
                                    <SelectItem value={NONE_VALUE} className="text-muted-foreground">(unset)</SelectItem>
                                    <SelectItem value={BOOL_TRUE}>Yes</SelectItem>
                                    <SelectItem value={BOOL_FALSE}>No</SelectItem>
                                </SelectContent>
                            </Select>
                        </div>
                    );
                }}
            />
        );
    }

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

function ContentField({ column, form }: { column: Column; form: AnyReactFormApi }) {
    const name = column.name;
    const isRequired = !column.isNullable;
    const errorId = `${name}-error`;
    return (
        <form.Field
            name={name}
            validators={{
                onSubmit: ({ value }: { value: unknown }) => validateFieldValue(column, value, isRequired),
                onBlur: ({ value }: { value: unknown }) => validateFieldValue(column, value, isRequired),
            }}
            children={(field: AnyFieldApi) => {
                const hasError = field.state.meta.errors.length > 0;
                return (
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
                        invalid={hasError}
                    />
                    <FieldErrors errors={field.state.meta.errors} id={errorId} />
                </div>
                );
            }}
        />
    );
}

interface ParentFieldProps {
    column: Column;
    join: Join;
    form: AnyReactFormApi;
    schema: Schema;
    isRequired: boolean;
}

function ParentField({ column, join, form, schema, isRequired }: ParentFieldProps) {
    const name = column.name;
    const parentData = useTableRef(schema, join.destinationTable, join.destinationColumnNames[0]);
    const [search, setSearch] = useState('');
    const errorId = `${name}-error`;

    const term = search.trim().toLowerCase();
    const filteredRows = term
        ? parentData.data.filter((r: TableRefValue) => String(r.label ?? r.key ?? '').toLowerCase().includes(term))
        : parentData.data;

    return (
        <form.Field
            name={name}
            validators={{
                onSubmit: ({ value }: { value: unknown }) => validateFieldValue(column, value, isRequired),
                onBlur: ({ value }: { value: unknown }) => validateFieldValue(column, value, isRequired),
            }}
            children={(field: AnyFieldApi) => {
                const hasError = field.state.meta.errors.length > 0;
                return (
                <div className="grid gap-1">
                    <Label htmlFor={name} className="text-xs text-muted-foreground">
                        {column.label}
                        {isRequired && <span className="text-destructive ml-0.5">*</span>}
                    </Label>
                    <Select
                        value={String(field.state.value ?? '')}
                        onValueChange={(val) => field.handleChange(val === NONE_VALUE ? null : val)}
                        onOpenChange={(open) => { if (!open) setSearch(''); }}
                    >
                        <SelectTrigger id={name} className="w-full h-8 text-sm" aria-invalid={hasError} aria-describedby={hasError ? errorId : undefined}>
                            <SelectValue placeholder={`Select ${column.label}`} />
                        </SelectTrigger>
                        <SelectContent>
                            {/* Search box for FK lists that can hold many parent rows.
                                Stop propagation only for typing keys so Radix's
                                typeahead doesn't hijack them — Escape (close) and
                                Arrow/Enter (move into the list) still pass through. */}
                            <div className="px-1 pb-1 sticky top-0 bg-popover z-10">
                                <Input
                                    value={search}
                                    onChange={(e) => setSearch(e.target.value)}
                                    onKeyDown={(e) => {
                                        if (e.key.length === 1 || e.key === 'Backspace' || e.key === 'Delete') {
                                            e.stopPropagation();
                                        }
                                    }}
                                    placeholder="Search…"
                                    className="h-7 text-xs"
                                />
                            </div>
                            {!isRequired && (
                                <SelectItem value={NONE_VALUE} className="text-muted-foreground">(none)</SelectItem>
                            )}
                            {filteredRows.map((row: TableRefValue) => (
                                <SelectItem key={row.key} value={String(row.key)}>
                                    {row.label}
                                </SelectItem>
                            ))}
                            {filteredRows.length === 0 && (
                                <div className="px-2 py-1.5 text-xs text-muted-foreground">No matches.</div>
                            )}
                        </SelectContent>
                    </Select>
                    <FieldErrors errors={field.state.meta.errors} id={errorId} />
                </div>
                );
            }}
        />
    );
}

interface CompositeParentFieldProps {
    column: Column;
    join: Join;
    form: AnyReactFormApi;
    schema: Schema;
    isRequired: boolean;
}

/**
 * Dropdown for composite foreign keys. One <Select> per FK (rendered on the anchor
 * column); selecting a parent row writes every `join.sourceColumnNames[i]` on the
 * form to the matching destination column value. Member columns render no input.
 */
function CompositeParentField({ column, join, form, schema, isRequired }: CompositeParentFieldProps) {
    const sourceCols = join.sourceColumnNames ?? [];
    const destCols = join.destinationColumnNames ?? [];
    const parents = useCompositeTableRef(schema, join.destinationTable, destCols);

    // Build the currently selected route from the form state of every source column.
    // useStore subscribes to the form's underlying store slice so the Select stays in
    // sync if other code paths mutate any source column directly. (`form.useStore` isn't
    // a real API — the hook lives on the package export and takes the store directly.)
    const currentRoute: string = useStore(form.store, (s: { values: Record<string, unknown> }) => {
        const vals: unknown[] = [];
        for (const c of sourceCols) {
            const v = s.values?.[c];
            if (v === undefined || v === null || v === '') return '';
            vals.push(v);
        }
        // Shared encoder with the row producer (useCompositeTableRef -> rowIdOf),
        // so the selected route always matches a parent row's route.
        return encodeRouteParts(vals);
    });

    const onChange = (route: string) => {
        const match = parents.data.find((r) => r.route === route);
        if (!match) return;
        for (let i = 0; i < sourceCols.length; i++) {
            const destCol = destCols[i];
            const srcCol = sourceCols[i];
            form.setFieldValue(srcCol, match.values[destCol]);
        }
    };

    const errorId = `${column.name}-error`;

    // Register a validator on the anchor source column so a required composite FK
    // can't submit empty. onChange writes every source column via setFieldValue,
    // so validating the anchor is sufficient to gate the whole FK.
    return (
        <form.Field
            name={column.name}
            validators={{
                onSubmit: ({ value }: { value: unknown }) => validateFieldValue(column, value, isRequired),
            }}
            children={(field: AnyFieldApi) => {
                const hasError = field.state.meta.errors.length > 0;
                return (
                <div className="grid gap-1 sm:col-span-2">
                    <Label htmlFor={column.name} className="text-xs text-muted-foreground">
                        {column.label}
                        {isRequired && <span className="text-destructive ml-0.5">*</span>}
                        <span className="ml-2 px-1 rounded bg-primary/10 text-[10px] font-medium text-primary">
                            composite FK ({sourceCols.length} cols)
                        </span>
                    </Label>
                    <Select value={currentRoute} onValueChange={onChange}>
                        <SelectTrigger id={column.name} className="w-full h-8 text-sm" aria-invalid={hasError} aria-describedby={hasError ? errorId : undefined}>
                            <SelectValue placeholder={`Select ${column.label}`} />
                        </SelectTrigger>
                        <SelectContent>
                            {parents.data.map((row) => (
                                <SelectItem key={row.route} value={row.route}>
                                    {row.label}
                                </SelectItem>
                            ))}
                        </SelectContent>
                    </Select>
                    <FieldErrors errors={field.state.meta.errors} id={errorId} />
                </div>
                );
            }}
        />
    );
}

function FieldErrors({ errors, id }: { errors: unknown[]; id?: string }) {
    if (errors.length === 0) return null;
    return (
        <div id={id} role="alert">
            {errors.map((error, i) => (
                <p key={i} className="text-xs text-destructive">
                    {String(error)}
                </p>
            ))}
        </div>
    );
}
