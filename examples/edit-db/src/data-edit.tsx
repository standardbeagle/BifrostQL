import { useQuery } from "@tanstack/react-query";
import { ReactElement, ReactNode, useEffect, useMemo, useState } from "react";
import { useForm, useStore, AnyFieldApi, ReactFormExtendedApi } from "@tanstack/react-form";
import { useSchema } from "./hooks/useSchema";
import { useParams, useNavigate } from "./hooks/usePath";
import { Schema, Table, Column, Join } from "./types/schema";
import { TableRefValue, useTableRef, useTableRefValue } from "./hooks/useTableRef";
import { useCompositeTableRef } from "./hooks/useCompositeTableRef";
import { useFetcher } from "./common/fetcher";
import { useTableMutation } from "./hooks/useTableMutation";
import { parsePkRoute, buildPkEqFilter, encodeRouteParts } from "./lib/row-id";
import { validateFieldValue } from "./lib/field-validation";
import { isComposite } from "./lib/fk";
import { isDateColumn, isDateTimeColumn, toDateInputValue, preserveUntouchedDateValues } from "./lib/date-input";
import { matchesLabel } from "./lib/label-match";
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
import { isBinaryDbType, isLongTextDbType, isJsonColumn } from "@/lib/content-detect";

const booleanTypes = ["Boolean", "Boolean!"];
const numericTypes = ["Int", "Int!", "Float", "Float!"];

// Sentinel for the "(none)" option in FK/enum selects. Radix's Select rejects an
// empty-string item value, so we round-trip this and map it back to null on change.
export const NONE_VALUE = "__none__";

/**
 * Maps a form value to the Radix `<Select>` control value. Radix shows the
 * placeholder for '', so a cleared/unset value must map to the "(none)" item's
 * sentinel whenever that item is offered — otherwise clearing a value would be
 * indistinguishable from never having picked one.
 */
export function selectControlValue(value: unknown, offersNone: boolean): string {
    if (value === null || value === undefined || value === '') return offersNone ? NONE_VALUE : '';
    return String(value);
}

/**
 * Shared onSubmit/onBlur validators for a form field — client and server enforce
 * identical rules through validateFieldValue, and blur gives per-field feedback
 * before the user hits Save.
 */
function fieldValidators(column: Column, isRequired: boolean) {
    const validate = ({ value }: { value: unknown }) => validateFieldValue(column, value, isRequired);
    return { onSubmit: validate, onBlur: validate };
}

/** Shared error/aria wiring for a form field's control. */
function fieldA11y(field: AnyFieldApi, errorId: string) {
    const hasError = field.state.meta.errors.length > 0;
    return {
        hasError,
        ariaProps: {
            'aria-invalid': hasError,
            'aria-describedby': hasError ? errorId : undefined,
        },
    };
}

/** Columns edited through the ContentEditor (textarea) rather than a plain input. */
function isContentColumn(c: Column): boolean {
    return isBinaryDbType(c.dbType) || isLongTextDbType(c.dbType) || isJsonColumn(c);
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
        const table = schema?.data?.find((x: { graphQlName: string | undefined; }) => x.graphQlName === tableName)!;
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

/**
 * Loads the record being edited (by its composite-aware PK route) and derives
 * everything the form needs to bind: the raw stored `value`, the form's
 * `defaultValues` (with type-correct NULL handling for dates, nullable booleans,
 * JSON, and FK/enum columns), and `pkResolveFailed` for a malformed/stale editid.
 */
function useEditRecord(dataTable: Table, editColumns: ColumnJoin[], editid: string) {
    const fetcher = useFetcher();
    const isInsert = editid === undefined || editid === '';

    const parsedPk = useMemo(
        () => (isInsert ? null : parsePkRoute(editid, dataTable)),
        [isInsert, editid, dataTable],
    );

    const pkEq = useMemo(() => {
        if (isInsert || !parsedPk) return EMPTY_PK_EQ_FILTER;
        return buildPkEqFilter(parsedPk, dataTable) ?? EMPTY_PK_EQ_FILTER;
    }, [isInsert, parsedPk, dataTable]);

    // A malformed or stale editid (bad composite arity, a PK-less table, an empty
    // segment) yields no filter. Rather than render a blank editable form backed by a
    // disabled query — which on save fires a keyless UPDATE against the whole table —
    // treat it as an unresolvable record and show an error state below.
    const pkResolveFailed = !isInsert && pkEq.filterText === "";

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
            if (isDateColumn(c)) {
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

    return { isLoading, error, value, defaultValues, pkResolveFailed };
}

function DataEditDetail({ table, schema, editid, onClose }: { table: string, schema: Schema, editid: string, onClose: () => void }) {
    const isInsert = editid === undefined || editid === '';
    const [dataTable, editColumns, idColumns] = useTable(schema, table);
    const labelColumn = dataTable.labelColumn;
    const label = dataTable.label;

    const mutation = useTableMutation(dataTable, editColumns, idColumns, editid);

    const { isLoading, error, value, defaultValues, pkResolveFailed } = useEditRecord(dataTable, editColumns, editid);

    const form = useForm({
        defaultValues,
        onSubmit: async ({ value: formValues }) => {
            // Date inputs hold a lossy projection of the stored value (timezone
            // offset and fractional seconds trimmed). For date fields the user
            // did not edit, send the original raw value so saving an unrelated
            // field cannot shift the stored instant.
            const detail = preserveUntouchedDateValues(
                formValues,
                value,
                editColumns.map(({ column: c }) => c),
            );
            const mutate = isInsert ? mutation.insert : mutation.update;
            await mutate(detail);
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

    // Refuse to render an editable form for a record we can't key. Saving one would
    // build an UPDATE with no WHERE columns (a full-table write).
    if (pkResolveFailed) {
        return (
            <Dialog open onOpenChange={(open) => { if (!open) onClose(); }}>
                <DialogContent showCloseButton={false} className="sm:max-w-md">
                    <DialogTitle className="text-sm font-semibold">Cannot edit record</DialogTitle>
                    <DialogDescription className="text-sm text-muted-foreground">
                        This record can't be opened for editing: its key ({editid || 'empty'}) is
                        missing or malformed for the {label} table. Return to the grid and try again.
                    </DialogDescription>
                    <div className="flex justify-end">
                        <Button type="button" size="sm" onClick={onClose}>Close</Button>
                    </div>
                </DialogContent>
            </Dialog>
        );
    }

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
    // A stale drill link or a bad :table route resolves to a table that is not
    // in the schema. Report it here — before mounting DataEditDetail, whose
    // useTable non-null-asserts the lookup and would otherwise throw a
    // TypeError (reading `.columns` of undefined) straight into the error
    // boundary instead of a readable message.
    if (!schema.findTable(table)) {
        return <div className="p-4 text-sm text-destructive">Table not found: {table}</div>;
    }

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
    const isDate = isDateColumn(column);
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

    const errorId = `${name}-error`;

    return (
        <form.Field
            name={name}
            validators={fieldValidators(column, isRequired)}
            children={(field: AnyFieldApi) => {
                const { ariaProps } = fieldA11y(field, errorId);
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
                        {...ariaProps}
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
            validators={fieldValidators(column, isRequired)}
            children={(field: AnyFieldApi) => {
                const { ariaProps } = fieldA11y(field, errorId);
                return (
                <div className="grid gap-1">
                    <Label htmlFor={name} className="text-xs text-muted-foreground">
                        {column.label}
                        {isRequired && <span className="text-destructive ml-0.5">*</span>}
                    </Label>
                    <Select
                        value={selectControlValue(field.state.value, !isRequired)}
                        onValueChange={(val) => field.handleChange(val === NONE_VALUE ? null : val)}
                    >
                        <SelectTrigger id={name} className="w-full h-8 text-sm" {...ariaProps}>
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
            validators={fieldValidators(column, isRequired)}
            children={(field: AnyFieldApi) => {
                const { hasError } = fieldA11y(field, errorId);
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

/**
 * Debounced search state shared by the FK dropdowns. The option-list fetch is
 * deferred until the dropdown opens (fetch-on-open) so a form with many FK
 * fields doesn't fire a query per field on dialog mount; the debounce keeps each
 * keystroke from firing a query. Closing the dropdown resets the search so the
 * next open starts clean.
 */
function useFkSearch() {
    const [search, setSearch] = useState('');
    const [debounced, setDebounced] = useState('');
    const [open, setOpen] = useState(false);

    useEffect(() => {
        const t = setTimeout(() => setDebounced(search), 300);
        return () => clearTimeout(t);
    }, [search]);

    const onOpenChange = (isOpen: boolean) => {
        setOpen(isOpen);
        if (!isOpen) { setSearch(''); setDebounced(''); }
    };

    return { search, setSearch, debounced, open, onOpenChange };
}

interface FkSelectShellProps {
    field: AnyFieldApi;
    column: Column;
    isRequired: boolean;
    errorId: string;
    /** Controlled Select value — the FK key (ParentField) or composite route (CompositeParentField). */
    value: string;
    onValueChange: (val: string) => void;
    search: string;
    setSearch: (search: string) => void;
    onOpenChange: (open: boolean) => void;
    /** Truthy when the option-list fetch failed — surfaced so an empty dropdown isn't mistaken for "no rows". */
    loadError: unknown;
    /** When set, renders the composite-FK badge and spans both grid columns. */
    composite?: { cols: number };
    /** The `<SelectItem>` option list (differs per FK kind). */
    children: ReactNode;
}

/**
 * Shared dropdown scaffold for single and composite FK fields: label (+ optional
 * composite badge), a searchable `<Select>`, the load-error message, and field
 * errors. The item list is supplied as `children`.
 */
function FkSelectShell({
    field,
    column,
    isRequired,
    errorId,
    value,
    onValueChange,
    search,
    setSearch,
    onOpenChange,
    loadError,
    composite,
    children,
}: FkSelectShellProps) {
    const { ariaProps } = fieldA11y(field, errorId);
    return (
        <div className={composite ? "grid gap-1 sm:col-span-2" : "grid gap-1"}>
            <Label htmlFor={column.name} className="text-xs text-muted-foreground">
                {column.label}
                {isRequired && <span className="text-destructive ml-0.5">*</span>}
                {composite && (
                    <span className="ml-2 px-1 rounded bg-primary/10 text-[10px] font-medium text-primary">
                        composite FK ({composite.cols} cols)
                    </span>
                )}
            </Label>
            <Select value={value} onValueChange={onValueChange} onOpenChange={onOpenChange}>
                <SelectTrigger id={column.name} className="w-full h-8 text-sm" {...ariaProps}>
                    <SelectValue placeholder={`Select ${column.label}`} />
                </SelectTrigger>
                <SelectContent>
                    {/* Search box for FK lists that can hold many parent rows.
                        Stop propagation only for typing keys so Radix's typeahead
                        doesn't hijack them — Escape (close) and Arrow/Enter (move
                        into the list) still pass through. */}
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
                    {children}
                </SelectContent>
            </Select>
            {/* Fail fast and visibly: a failed option fetch otherwise reads
                as an inexplicably empty dropdown. */}
            {loadError != null && (
                <p className="text-xs text-destructive" role="alert">
                    Failed to load {column.label} options: {String((loadError as Error)?.message ?? loadError)}
                </p>
            )}
            <FieldErrors errors={field.state.meta.errors} id={errorId} />
        </div>
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
    const destColumn = join.destinationColumnNames[0];
    const { search, setSearch, debounced, open, onOpenChange } = useFkSearch();

    const parentData = useTableRef(schema, join.destinationTable, destColumn, debounced, open);

    // Subscribe to the current FK value at hook level (form.Field's children is
    // a render prop, so hooks can't live inside it).
    const currentValue = useStore(form.store, (s: { values: Record<string, unknown> }) => s.values?.[name]);
    const currentInWindow = currentValue == null || currentValue === ''
        || parentData.data.some((r: TableRefValue) => String(r.key) === String(currentValue));
    // When the stored FK points outside the fetched window, resolve its label
    // with a single-row lookup so the Select shows the stored value instead of
    // a misleading placeholder.
    const lookup = useTableRefValue(
        schema,
        join.destinationTable,
        destColumn,
        !parentData.loading && !currentInWindow ? currentValue : null,
    );

    const errorId = `${name}-error`;

    const term = debounced.trim();
    const filteredRows = parentData.serverSearch || !term
        ? parentData.data
        : parentData.data.filter((r: TableRefValue) => matchesLabel(r, 'key', term));

    // The currently selected parent must always have a matching item so the
    // closed Select can display it — even when it's outside the fetched window
    // (lookup above) or filtered out by the search term. Until the lookup
    // resolves (or if the FK dangles), fall back to showing the raw key.
    const selectedRow: TableRefValue | null = currentValue == null || currentValue === ''
        ? null
        : parentData.data.find((r: TableRefValue) => String(r.key) === String(currentValue))
            ?? lookup.value
            ?? { key: String(currentValue), label: String(currentValue) };

    return (
        <form.Field
            name={name}
            validators={fieldValidators(column, isRequired)}
            children={(field: AnyFieldApi) => (
                <FkSelectShell
                    field={field}
                    column={column}
                    isRequired={isRequired}
                    errorId={errorId}
                    value={selectControlValue(field.state.value, !isRequired)}
                    onValueChange={(val) => field.handleChange(val === NONE_VALUE ? null : val)}
                    search={search}
                    setSearch={setSearch}
                    onOpenChange={onOpenChange}
                    loadError={parentData.error}
                >
                    {!isRequired && (
                        <SelectItem value={NONE_VALUE} className="text-muted-foreground">(none)</SelectItem>
                    )}
                    {selectedRow && !filteredRows.some((r: TableRefValue) => String(r.key) === String(selectedRow.key)) && (
                        <SelectItem value={String(selectedRow.key)}>{selectedRow.label}</SelectItem>
                    )}
                    {filteredRows.map((row: TableRefValue) => (
                        <SelectItem key={row.key} value={String(row.key)}>
                            {row.label}
                        </SelectItem>
                    ))}
                    {filteredRows.length === 0 && (
                        <div className="px-2 py-1.5 text-xs text-muted-foreground">No matches.</div>
                    )}
                </FkSelectShell>
            )}
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

    const { search, setSearch, debounced, open, onOpenChange } = useFkSearch();

    // The current selection keyed by DESTINATION column name (the hook resolves and
    // sorts by destination columns). Form values are stored by SOURCE column, paired
    // positionally with destination columns.
    const currentValues: Record<string, unknown> = useStore(
        form.store,
        (s: { values: Record<string, unknown> }) => {
            const out: Record<string, unknown> = {};
            for (let i = 0; i < sourceCols.length; i++) out[destCols[i]] = s.values?.[sourceCols[i]];
            return out;
        },
    );

    const parents = useCompositeTableRef(schema, join.destinationTable, destCols, {
        search: debounced,
        enabled: open,
        currentValues,
    });

    // Build the currently selected route from the form state of every source column.
    // Shared encoder with the row producer (useCompositeTableRef -> rowIdOf), so the
    // selected route always matches a parent row's route.
    const currentRoute: string = useMemo(() => {
        const vals: unknown[] = [];
        for (let i = 0; i < sourceCols.length; i++) {
            const v = currentValues[destCols[i]];
            if (v === undefined || v === null || v === '') return '';
            vals.push(v);
        }
        return encodeRouteParts(vals);
    }, [currentValues, sourceCols, destCols]);

    const term = debounced.trim();
    const filteredRows = parents.serverSearch || !term
        ? parents.data
        : parents.data.filter((r) => r.label.toLowerCase().includes(term.toLowerCase()));

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
            validators={fieldValidators(column, isRequired)}
            children={(field: AnyFieldApi) => (
                <FkSelectShell
                    field={field}
                    column={column}
                    isRequired={isRequired}
                    errorId={errorId}
                    value={currentRoute}
                    onValueChange={onChange}
                    search={search}
                    setSearch={setSearch}
                    onOpenChange={onOpenChange}
                    loadError={parents.error}
                    composite={{ cols: sourceCols.length }}
                >
                    {filteredRows.map((row) => (
                        <SelectItem key={row.route} value={row.route}>
                            {row.label}
                        </SelectItem>
                    ))}
                    {filteredRows.length === 0 && (
                        <div className="px-2 py-1.5 text-xs text-muted-foreground">No matches.</div>
                    )}
                </FkSelectShell>
            )}
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
