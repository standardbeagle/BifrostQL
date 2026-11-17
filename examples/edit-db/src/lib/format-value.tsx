import type { ReactNode } from 'react';
import { Check, X } from 'lucide-react';
import { EmptyValue } from '../components/empty-value';
import type { Column } from '../types/schema';

/**
 * Per-column display formatting for grid/detail cells.
 *
 * Raw DB values — especially SQL Server datetimes like
 * `2026-05-11T22:17:47.7636626` — are unreadable. This formats values into
 * concise, locale-aware forms, driven by a `display-format` column metadata key
 * with a sensible default inferred from the column type when none is set.
 *
 * Supported `display-format` values:
 *   date      — locale date              (e.g. "May 11, 2026")
 *   datetime  — locale date + time       (e.g. "May 11, 2026, 10:17 PM")
 *   time      — locale time              (e.g. "10:17 PM")
 *   relative  — humanized relative time  (e.g. "4 hours ago"), hover shows exact
 *   number    — grouped locale number    (e.g. "1,234,567")
 *   percent   — locale percent           (0.42 -> "42%")
 *   raw       — no formatting (escape hatch)
 *
 * Any formatted cell keeps the exact value in a `title` so hovering reveals the
 * precise underlying data.
 */

export const DISPLAY_FORMAT_KEY = 'display-format';
/**
 * Secondary, context-specific format template (used by compact surfaces like FK
 * preview popovers). When a column doesn't set it, formatting falls back to the
 * main `display-format`, so the preview matches the grid by default.
 */
export const DISPLAY_FORMAT_PREVIEW_KEY = 'display-format-preview';

export type DisplayFormat = 'date' | 'datetime' | 'time' | 'relative' | 'number' | 'percent' | 'raw';

const KNOWN: ReadonlySet<string> = new Set(['date', 'datetime', 'time', 'relative', 'number', 'percent', 'raw']);

/** Options for {@link formatColumnValue}. */
export interface FormatOptions {
    /**
     * Metadata key to read the format from before falling back to the main
     * `display-format`. Lets a context override the template while defaulting to
     * the main one when the column doesn't set the override key.
     */
    formatKey?: string;
}

/** Base GraphQL/db type, lower-cased, with the `!` non-null suffix stripped. */
function baseType(column: Column): { param: string; db: string } {
    return {
        param: (column.paramType ?? '').replace('!', '').toLowerCase(),
        db: (column.dbType ?? '').toLowerCase(),
    };
}

function isDateType({ param, db }: { param: string; db: string }): boolean {
    return param === 'date' || db === 'date';
}
function isDateTimeType({ param, db }: { param: string; db: string }): boolean {
    return param === 'datetime' || param === 'datetimeoffset'
        || db.includes('datetime') || db === 'timestamp' || db === 'smalldatetime';
}
/**
 * Resolve the effective format: an explicit override key wins, then the main
 * `display-format` metadata, else inferred from the column type. Passing
 * `formatKey` lets a context default to the main template when its own is unset.
 */
export function resolveDisplayFormat(column: Column, formatKey?: string): DisplayFormat | null {
    const override = formatKey ? column.metadata?.[formatKey] : undefined;
    const meta = override ?? column.metadata?.[DISPLAY_FORMAT_KEY];
    if (typeof meta === 'string' && KNOWN.has(meta.toLowerCase())) {
        return meta.toLowerCase() as DisplayFormat;
    }
    const t = baseType(column);
    if (isDateTimeType(t)) return 'datetime';
    if (isDateType(t)) return 'date';
    return null; // no special formatting
}

const DATE_ONLY = /^(\d{4})-(\d{2})-(\d{2})$/;

/**
 * A calendar date (`2026-05-11`, what a `date` column sends) names a day, not
 * an instant. `new Date('2026-05-11')` reads it as UTC midnight, which local
 * formatting shows as May 10 anywhere west of UTC, so it is built as LOCAL
 * midnight instead. setFullYear keeps years below 100 literal (the Date
 * constructor maps 0-99 to 1900-1999).
 */
function toDate(value: unknown): Date | null {
    if (value instanceof Date) return isNaN(value.getTime()) ? null : value;
    if (typeof value === 'number') { const d = new Date(value); return isNaN(d.getTime()) ? null : d; }
    if (typeof value === 'string') {
        const day = DATE_ONLY.exec(value);
        if (day) {
            const d = new Date(0);
            d.setFullYear(Number(day[1]), Number(day[2]) - 1, Number(day[3]));
            d.setHours(0, 0, 0, 0);
            return isNaN(d.getTime()) ? null : d;
        }
        const d = new Date(value);
        return isNaN(d.getTime()) ? null : d;
    }
    return null;
}

/**
 * The two values a date column stores to mean "not set": the Unix epoch
 * (timestamp 0, or the calendar date 1970-01-01) and .NET/SQL
 * DateTime.MinValue (year 0001). Only these exact markers count; real
 * historical dates (1900, 1969) are real data.
 */
function isUnsetSentinel(date: Date, raw: unknown): boolean {
    if (typeof raw === 'string' && DATE_ONLY.test(raw)) return raw === '1970-01-01' || date.getFullYear() <= 1;
    return date.getTime() === 0 || date.getUTCFullYear() <= 1;
}

const dateFmt = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' });
const dateTimeFmt = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' });
const timeFmt = new Intl.DateTimeFormat(undefined, { timeStyle: 'short' });
const exactFmt = new Intl.DateTimeFormat(undefined, { dateStyle: 'full', timeStyle: 'medium' });
const relFmt = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' });

const REL_UNITS: [Intl.RelativeTimeFormatUnit, number][] = [
    ['year', 31536000], ['month', 2592000], ['week', 604800],
    ['day', 86400], ['hour', 3600], ['minute', 60], ['second', 1],
];

/** "4 hours ago" / "in 3 days" via Intl.RelativeTimeFormat. */
export function formatRelative(date: Date, now: Date = new Date()): string {
    const diffSec = Math.round((date.getTime() - now.getTime()) / 1000);
    const abs = Math.abs(diffSec);
    for (const [unit, secs] of REL_UNITS) {
        if (abs >= secs || unit === 'second') {
            return relFmt.format(Math.round(diffSec / secs), unit);
        }
    }
    return relFmt.format(0, 'second');
}

/**
 * Display labels for an enum column, or null when it has none that can be
 * trusted. `enum-labels` are positional against `enum-values`, so a count
 * mismatch would mislabel every value; it is treated as no labels at all.
 */
export function usableEnumLabels(column: Column): string[] | null {
    const values = column.enumValues;
    const labels = column.enumLabels;
    if (!values?.length || !labels || labels.length !== values.length) return null;
    return labels;
}

/**
 * Value-to-label lookup for an enum column, or null when it has no usable labels.
 * The first occurrence of a repeated value wins, and an empty label is no label.
 */
function enumLabelMap(column: Column): ReadonlyMap<string, string> | null {
    const labels = usableEnumLabels(column);
    if (!labels) return null;
    const map = new Map<string, string>();
    column.enumValues!.forEach((value, index) => {
        if (!map.has(value)) map.set(value, labels[index]);
    });
    for (const [value, label] of map) if (!label) map.delete(value);
    return map;
}

/** The label for one enum value, or null when the column has no usable labels or the value is not one of them. */
export function enumLabelFor(value: unknown, column: Column): string | null {
    return enumLabelMap(column)?.get(String(value)) ?? null;
}

// Intl formatters are expensive to construct (hundreds of µs) and cheap to call, so
// every one a cell uses is built once here.
const numberFmt = new Intl.NumberFormat(undefined);
const DECIMAL_TEXT = /^-?\d+(?:\.\d+)?$/;
const percentFmt = new Intl.NumberFormat(undefined, { style: 'percent', maximumFractionDigits: 2 });

/** Renders one cell value of a column. Built once per column by {@link columnFormatter}. */
export type CellFormatter = (value: unknown) => ReactNode;

/** Renders a non-empty value that is not an enum label, for one resolved format. */
function formatterFor(fmt: DisplayFormat | null): (value: unknown) => ReactNode {
    switch (fmt) {
        case 'date':
        case 'datetime':
        case 'time': {
            const display = fmt === 'date' ? dateFmt : fmt === 'time' ? timeFmt : dateTimeFmt;
            return (value) => {
                const date = toDate(value);
                if (!date) return String(value); // unparseable → show raw rather than hide it
                if (isUnsetSentinel(date, value)) return <EmptyValue kind="unset" title={String(value)} />;
                return <span title={exactFmt.format(date)}>{display.format(date)}</span>;
            };
        }
        case 'relative':
            return (value) => {
                const date = toDate(value);
                if (!date) return String(value);
                if (isUnsetSentinel(date, value)) return <EmptyValue kind="unset" title={String(value)} />;
                return (
                    <span title={exactFmt.format(date)} className="cursor-help underline decoration-dotted decoration-muted-foreground/40 underline-offset-2">
                        {formatRelative(date)}
                    </span>
                );
            };
        case 'number':
        case 'percent': {
            const display = fmt === 'percent' ? percentFmt : numberFmt;
            return (value) => {
                // The server sends BigInt and Decimal as decimal strings. Intl
                // formats a numeric string exactly; through Number() a value past
                // 15–17 significant digits would display different digits.
                if (typeof value === 'string' && DECIMAL_TEXT.test(value)) {
                    return <span title={value}>{display.format(value as Intl.StringNumericLiteral)}</span>;
                }
                const num = typeof value === 'number' ? value : Number(value);
                if (isNaN(num)) return String(value);
                return <span title={String(value)}>{display.format(num)}</span>;
            };
        }
        default:
            return (value) => String(value);
    }
}

/**
 * A check or a cross for a boolean value, with the value itself as hover text and
 * screen-reader text. Anything that is not a JSON boolean renders raw, so a
 * mistyped value stays visible instead of reading as false.
 */
function formatBoolean(value: unknown): ReactNode {
    if (typeof value !== 'boolean') return String(value);
    const Icon = value ? Check : X;
    return (
        <span title={String(value)} className={value ? 'inline-flex text-foreground' : 'inline-flex text-muted-foreground'}>
            <Icon className="size-3.5" aria-hidden="true" />
            <span className="sr-only">{String(value)}</span>
        </span>
    );
}

function compileColumnFormatter(column: Column, formatKey: string | undefined): CellFormatter {
    // Enum columns often store codes (0, 1, 2) whose meaning lives in the labels.
    const labels = enumLabelMap(column);
    const displayFormat = resolveDisplayFormat(column, formatKey);
    // A boolean column with no explicit format renders as a glyph.
    const format = displayFormat === null && baseType(column).param === 'boolean'
        ? formatBoolean
        : formatterFor(displayFormat);
    return (value) => {
        if (value === null || value === undefined) return <EmptyValue kind="null" />;
        if (value === '') return <EmptyValue kind="empty" />;
        const label = labels?.get(String(value));
        if (label !== undefined) return <span title={String(value)}>{label}</span>;
        return format(value);
    };
}

// Keyed by the column object: schema columns are rebuilt, never mutated, when the
// schema reloads, so a stale formatter cannot outlive the column it was built from.
const formatters = new WeakMap<Column, Map<string, CellFormatter>>();

/**
 * The cell formatter for a column: resolves its display format, enum labels and Intl
 * formatters once, so rendering a cell is a lookup and one format call. See
 * {@link formatColumnValue} for what it renders.
 */
export function columnFormatter(column: Column, formatKey?: string): CellFormatter {
    let byKey = formatters.get(column);
    if (!byKey) {
        byKey = new Map();
        formatters.set(column, byKey);
    }
    const key = formatKey ?? '';
    let formatter = byKey.get(key);
    if (!formatter) {
        formatter = compileColumnFormatter(column, formatKey);
        byKey.set(key, formatter);
    }
    return formatter;
}

/**
 * Format a scalar value for display, given its column. Returns a muted
 * placeholder for null/empty, the enum label for a labelled enum value (with
 * the stored value in `title`), a locale-formatted node for known formats (with
 * a `title` revealing the exact value), or the plain string otherwise.
 */
export function formatColumnValue(value: unknown, column: Column, options?: FormatOptions): ReactNode {
    return columnFormatter(column, options?.formatKey)(value);
}

/**
 * Formats a number into an abbreviated string representation.
 *
 * @example
 * ```typescript
 * abbreviateNumber(12)      // "12"
 * abbreviateNumber(1200)    // "1.2k"
 * abbreviateNumber(45000)   // "45k"
 * abbreviateNumber(1200000) // "1.2M"
 * ```
 *
 * @param num - Number to abbreviate
 * @returns Abbreviated string representation
 */
export function abbreviateNumber(num: number | null): string {
    if (num === null) return "—";
    if (num === 0) return "0";
    if (num < 1000) return num.toString();
    if (num < 1000000) {
        const k = num / 1000;
        return k % 1 === 0 ? `${k.toFixed(0)}k` : `${k.toFixed(1)}k`;
    }
    const m = num / 1000000;
    return m % 1 === 0 ? `${m.toFixed(0)}M` : `${m.toFixed(1)}M`;
}
