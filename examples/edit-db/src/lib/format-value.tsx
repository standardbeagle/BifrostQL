import type { ReactNode } from 'react';
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

export type DisplayFormat = 'date' | 'datetime' | 'time' | 'relative' | 'number' | 'percent' | 'raw';

const KNOWN: ReadonlySet<string> = new Set(['date', 'datetime', 'time', 'relative', 'number', 'percent', 'raw']);

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
/** Resolve the effective format: explicit metadata wins, else inferred from type. */
export function resolveDisplayFormat(column: Column): DisplayFormat | null {
    const meta = column.metadata?.[DISPLAY_FORMAT_KEY];
    if (typeof meta === 'string' && KNOWN.has(meta.toLowerCase())) {
        return meta.toLowerCase() as DisplayFormat;
    }
    const t = baseType(column);
    if (isDateTimeType(t)) return 'datetime';
    if (isDateType(t)) return 'date';
    return null; // no special formatting
}

function toDate(value: unknown): Date | null {
    if (value instanceof Date) return isNaN(value.getTime()) ? null : value;
    if (typeof value === 'number') { const d = new Date(value); return isNaN(d.getTime()) ? null : d; }
    if (typeof value === 'string') { const d = new Date(value); return isNaN(d.getTime()) ? null : d; }
    return null;
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
 * Format a scalar value for display, given its column. Returns a muted
 * placeholder for null/empty, a locale-formatted node for known formats (with a
 * `title` revealing the exact value), or the plain string otherwise.
 */
export function formatColumnValue(value: unknown, column: Column): ReactNode {
    if (value === null || value === undefined) return <EmptyValue kind="null" />;
    if (value === '') return <EmptyValue kind="empty" />;

    const fmt = resolveDisplayFormat(column);
    if (fmt === null || fmt === 'raw') return String(value);

    if (fmt === 'date' || fmt === 'datetime' || fmt === 'time' || fmt === 'relative') {
        const date = toDate(value);
        if (!date) return String(value); // unparseable → show raw rather than hide it
        const exact = exactFmt.format(date);
        if (fmt === 'relative') {
            return (
                <span title={exact} className="cursor-help underline decoration-dotted decoration-muted-foreground/40 underline-offset-2">
                    {formatRelative(date)}
                </span>
            );
        }
        const text = (fmt === 'date' ? dateFmt : fmt === 'time' ? timeFmt : dateTimeFmt).format(date);
        return <span title={exact}>{text}</span>;
    }

    if (fmt === 'number' || fmt === 'percent') {
        const num = typeof value === 'number' ? value : Number(value);
        if (isNaN(num)) return String(value);
        const text = fmt === 'percent'
            ? new Intl.NumberFormat(undefined, { style: 'percent', maximumFractionDigits: 2 }).format(num)
            : new Intl.NumberFormat(undefined).format(num);
        return <span title={String(value)}>{text}</span>;
    }

    return String(value);
}
