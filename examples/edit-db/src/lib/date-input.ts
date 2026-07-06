/**
 * @module lib/date-input
 *
 * Date/datetime form-input helpers. `<input type="date|datetime-local">` can
 * only hold a lossy projection of a stored SQL value — the timezone offset and
 * fractional seconds do not fit the input format — so these helpers centralize
 * that projection and, on save, restore the original raw value for date fields
 * the user never edited (otherwise saving an unrelated field would rewrite a
 * stored '…+02:00' instant as an offset-less string and shift it).
 */
import type { Column } from '../types/schema';
import { resolveDisplayFormat } from './format-value';

const DATE_PARAM_TYPES = new Set(['DateTime', 'DateTime!']);

/** True for any date-ish column (bare date or datetime). */
export function isDateColumn(column: Column): boolean {
    return DATE_PARAM_TYPES.has(column.paramType);
}

/** True when a date-ish column carries a time-of-day component (datetime, not a bare date). */
export function isDateTimeColumn(column: Column): boolean {
    return resolveDisplayFormat(column) === 'datetime';
}

/**
 * Normalizes a stored date/datetime string into the value an `<input type="date">`
 * or `<input type="datetime-local">` expects. For datetime columns the time-of-day
 * is preserved (fractional seconds and timezone offset are trimmed) so re-saving an
 * unrelated field does not overwrite the time with midnight. Returns '' for the
 * SQL "zero" date and unparseable input.
 */
export function toDateInputValue(raw: string | undefined, withTime: boolean): string {
    if (!raw) return '';
    const m = raw.match(/^(\d{4}-\d{2}-\d{2})(?:[T ](\d{2}:\d{2}(?::\d{2})?))?/);
    if (!m) return '';
    if (m[1] === '0001-01-01') return '';
    if (!withTime) return m[1];
    if (!m[2]) return m[1]; // datetime column with no time part stored
    const time = m[2].length === 5 ? `${m[2]}:00` : m[2];
    return `${m[1]}T${time}`;
}

/**
 * Restore the original raw value for every date/datetime field the user did not
 * edit. The form holds `toDateInputValue` projections; a field whose current form
 * value still equals the projection of the original raw value is untouched, so
 * the raw value (offset and fractional seconds intact) is sent instead of the
 * lossy projection. Fields the user actually changed keep the new form value.
 */
export function preserveUntouchedDateValues(
    formValues: Record<string, unknown>,
    originalRow: Record<string, unknown>,
    columns: Column[],
): Record<string, unknown> {
    const out = { ...formValues };
    for (const column of columns) {
        if (!isDateColumn(column)) continue;
        const raw = originalRow[column.name];
        if (typeof raw !== 'string' || raw === '') continue;
        if (toDateInputValue(raw, isDateTimeColumn(column)) === out[column.name]) {
            out[column.name] = raw;
        }
    }
    return out;
}
