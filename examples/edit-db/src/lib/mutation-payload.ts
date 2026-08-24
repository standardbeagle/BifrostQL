import { Column, Table } from "../types/schema";
import { pkFilterFor, type PkFilter } from "./row-id";
import { validateRowValues } from "./field-validation";
import { isJsonColumn } from "./content-detect";
import { baseParamType, isExactScalar, isIntegerScalar, isNumericScalar } from "./scalar-types";

// BigInt/Decimal exceed what a JS number holds exactly: coercing through +val /
// Number() silently rounds a large key and drops decimal precision. The read path
// (fk.ts / row-id.ts coerceForGql) passes them as strings, so the write path must
// too — otherwise an edited key targets the wrong row. Every OTHER numeric scalar
// is declared as a type that rejects a string and must arrive as a number; Short
// and Byte (smallint/tinyint) used to miss this list entirely and were written as
// strings their `Short!`/`Byte!` input rejects.
const isPlainNumeric = (paramType: string) => isNumericScalar(paramType) && !isExactScalar(paramType);
const booleanTypes = ["Boolean", "Boolean!"];

export function coerceNumericValue(value: unknown, paramType: string, columnName: string): number {
    const baseType = baseParamType(paramType);
    const wholeNumber = isIntegerScalar(paramType);
    if (typeof value === 'number') {
        if (Number.isFinite(value) && (!wholeNumber || Number.isInteger(value))) return value;
        throw new Error(`Invalid ${baseType} value for column '${columnName}'.`);
    }

    const text = String(value).trim();
    const valid = wholeNumber
        ? /^[+-]?\d+$/.test(text)
        : /^[+-]?(?:(?:\d+\.?\d*)|(?:\.\d+))(?:[eE][+-]?\d+)?$/.test(text);
    if (!valid) throw new Error(`Invalid ${baseType} value for column '${columnName}'.`);
    const parsed = Number(text);
    if (!Number.isFinite(parsed)) throw new Error(`Invalid ${baseType} value for column '${columnName}'.`);
    return parsed;
}

export interface ColumnJoin {
    column: Column;
}

/**
 * Coerces a form/grid detail object into a wire-ready mutation payload: numeric
 * columns become numbers, BigInt/Decimal stay strings (precision beyond 2^53),
 * booleans and JSON columns get their scalar shapes, empty values follow the
 * insert-vs-update semantics (omit so DB defaults apply vs null to clear), and —
 * for updates — the primary-key columns are overwritten from the resolved
 * <paramref>pkFilter</paramref> so a payload can never retarget another row.
 * Shared by the single-row hook and the bulk delta paths.
 */
export function coerceDetail(
    detail: Record<string, unknown>,
    editColumns: ColumnJoin[],
    idColumns: Column[],
    pkFilter: PkFilter | null,
    isInsert: boolean
): Record<string, unknown> {
    const coerced = { ...detail };
    for (const { column: col } of editColumns) {
        // Explicit NULL (e.g. an FK/enum cleared via "(none)") — send null on
        // update to clear it, omit on insert so the DB default applies.
        if (coerced[col.name] === null) {
            coerced[col.name] = isInsert ? undefined : null;
            continue;
        }
        if (isPlainNumeric(col.paramType)) {
            const val = coerced[col.name];
            // An empty field means "no value": on update clear it with null
            // rather than coercing "" to 0 (a silent, wrong data write); on
            // insert omit the column entirely (undefined) so an explicit null
            // doesn't bypass the column's DB default. null/undefined stay
            // undefined so inserts omit the column entirely.
            coerced[col.name] = val == null ? undefined
                : val === "" ? (isInsert ? undefined : null)
                : coerceNumericValue(val, col.paramType, col.name);
        }
        if (isExactScalar(col.paramType)) {
            const val = coerced[col.name];
            // Same empty-value semantics as numeric, but the value itself is
            // passed as a string to preserve precision beyond 2^53.
            coerced[col.name] = val == null ? undefined
                : val === "" ? (isInsert ? undefined : null)
                : String(val);
        }
        if (booleanTypes.some(t => t === col.paramType)) {
            const v = coerced[col.name];
            // Nullable booleans keep NULL rather than coercing an unset value to
            // false. On insert an unset value is omitted so the DB default applies.
            if (col.isNullable && (v === null || v === undefined)) {
                coerced[col.name] = isInsert ? undefined : null;
            } else {
                coerced[col.name] = !!v;
            }
        }
        if (isJsonColumn(col)) {
            // The form edits JSON columns as text; parse back to a JSON value so
            // the JSON scalar isn't fed a double-encoded string. Unparseable text
            // is left as-is for the server to reject.
            const v = coerced[col.name];
            if (typeof v === 'string' && v.trim() !== '') {
                try { coerced[col.name] = JSON.parse(v); } catch { /* server validates */ }
            }
        }
    }
    if (!isInsert && pkFilter) {
        for (const col of idColumns) {
            const raw = pkFilter[col.name];
            if (isPlainNumeric(col.paramType)) {
                coerced[col.name] = raw == null ? null : coerceNumericValue(raw, col.paramType, col.name);
            } else {
                // Strings — including BigInt/Decimal PKs, which must stay strings so
                // a key above 2^53 targets the exact row it was read from.
                coerced[col.name] = raw;
            }
        }
    }
    return coerced;
}

/**
 * Builds the `updated:` payload list for a bulk edit: for each FRESH row
 * snapshot, echo every write-set column from the snapshot (the server's
 * `Update_<t>` input requires all non-nullable columns), overlay the shared
 * change set, validate, and coerce — with the primary key taken from the
 * snapshot itself, composite-safe, so a payload can never retarget another row.
 * Throws (never silently skips) when a row's key cannot be resolved or a merged
 * payload fails validation: a bulk edit either stages every selected row or none.
 */
export function buildBulkUpdatePayloads(
    table: Pick<Table, 'primaryKeys'>,
    writeColumns: Column[],
    idColumns: Column[],
    freshRows: readonly Record<string, unknown>[],
    changes: Record<string, unknown>,
): Record<string, unknown>[] {
    const editColumns: ColumnJoin[] = writeColumns.map((column) => ({ column }));
    return freshRows.map((row) => {
        const pkFilter = pkFilterFor(row, table);
        if (!pkFilter) throw new Error('Cannot bulk edit: a selected row has no resolvable primary key.');
        const detail: Record<string, unknown> = {};
        for (const column of writeColumns) detail[column.name] = row[column.name] ?? null;
        Object.assign(detail, changes);
        const validationErrors = validateRowValues(writeColumns, detail);
        if (validationErrors.length > 0) throw new Error(validationErrors.join(' '));
        return coerceDetail(detail, editColumns, idColumns, pkFilter, false);
    });
}

/**
 * Builds the `updated:` payload list for STAGED inline cell edits: unlike the
 * shared-change bulk edit, each row carries its OWN change map. Per fresh row:
 * the write set is every non-nullable editable column plus that row's changed
 * columns (the server's `Update_<t>` input requires the non-nullable echo), the
 * snapshot fills the echo, the row's changes overlay it, and the primary key
 * comes from the snapshot itself. Throws — never skips — when a fetched row has
 * no staged changes or a merged payload fails validation: Save all either
 * stages every edited row or none.
 */
export function buildStagedUpdatePayloads(
    table: Pick<Table, 'primaryKeys'>,
    editableColumns: Column[],
    idColumns: Column[],
    freshRows: readonly Record<string, unknown>[],
    changesForRow: (row: Record<string, unknown>) => Record<string, unknown> | undefined,
): Record<string, unknown>[] {
    return freshRows.map((row) => {
        const changes = changesForRow(row);
        if (!changes || Object.keys(changes).length === 0)
            throw new Error('Cannot save: a fetched row carries no staged changes.');
        const pkFilter = pkFilterFor(row, table);
        if (!pkFilter) throw new Error('Cannot save: an edited row has no resolvable primary key.');
        const writeColumns = editableColumns.filter((c) => !c.isNullable || c.name in changes);
        const detail: Record<string, unknown> = {};
        for (const column of writeColumns) detail[column.name] = row[column.name] ?? null;
        Object.assign(detail, changes);
        const validationErrors = validateRowValues(writeColumns, detail);
        if (validationErrors.length > 0) throw new Error(validationErrors.join(' '));
        return coerceDetail(detail, writeColumns.map((column) => ({ column })), idColumns, pkFilter, false);
    });
}
