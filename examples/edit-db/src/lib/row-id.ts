import type { Column, Table } from '../types/schema';

export type PkFilter = Record<string, unknown>;

export interface PkEqFilterResult {
    filterText: string;
    variables: Record<string, unknown>;
    params: string[];
}

type TableLike = Pick<Table, 'primaryKeys'> & Partial<Pick<Table, 'columns'>>;

const DELIMITER = '::';

function encodePkPart(value: unknown): string {
    if (value === null || value === undefined) return '';
    // encodeURIComponent escapes ':' to '%3A', so a key value containing the
    // '::' delimiter can never survive into the joined route to mis-split it.
    // (Round-trip covered by row-id.test.ts "round-trips PK values containing
    // the delimiter".)
    return encodeURIComponent(String(value));
}

function decodePkPart(raw: string): string | null {
    if (raw === '') return null;
    // Malformed percent-encoding (e.g. a bare "%") makes decodeURIComponent
    // throw URIError. A hostile or hand-edited route must not crash the
    // component tree — fall back to the raw segment instead.
    try {
        return decodeURIComponent(raw);
    } catch {
        return raw;
    }
}

/**
 * Encodes an ordered list of key values into a route string. The single source
 * of truth for composite-key route encoding — every producer (row lists) and
 * consumer (composite-FK selects) must funnel through this so the two sides can
 * never drift apart and silently fail to match.
 */
export function encodeRouteParts(values: readonly unknown[]): string {
    return values.map(encodePkPart).join(DELIMITER);
}

/**
 * Inverse of {@link encodeRouteParts}: splits a route-encoded id into its ordered
 * values and percent-decodes each. `expectedCount <= 1` treats the whole string as a
 * single value (never split, so a value that itself contains the delimiter survives).
 * Throws when a multi-part id does not carry exactly `expectedCount` parts, so a caller
 * building a composite filter cannot silently under-constrain it.
 */
export function decodeRouteParts(raw: string, expectedCount: number): string[] {
    const parts = expectedCount <= 1 ? [raw] : raw.split(DELIMITER);
    if (parts.length !== expectedCount)
        throw new Error(
            `Route id '${raw}' has ${parts.length} part(s) but ${expectedCount} were expected.`,
        );
    return parts.map((p) => (p === '' ? '' : (decodePkPart(p) ?? '')));
}

export function rowIdOf(
    row: Record<string, unknown> | null | undefined,
    table: TableLike,
    index: number,
): string {
    const keys = table.primaryKeys ?? [];
    if (keys.length === 0) return `row-${index}`;
    return encodeRouteParts(keys.map((pk) => row?.[pk]));
}

export function pkFilterFor(
    row: Record<string, unknown> | null | undefined,
    table: TableLike,
): PkFilter | null {
    const keys = table.primaryKeys ?? [];
    if (keys.length === 0 || row == null) return null;
    const filter: PkFilter = {};
    for (const pk of keys) {
        if (!(pk in row)) return null;
        filter[pk] = row[pk];
    }
    return filter;
}

function coerceForGql(value: unknown, gqlType: string): unknown {
    if (value === null || value === undefined) return null;
    switch (gqlType) {
        case 'Int': {
            const n = typeof value === 'number' ? value : Number(value);
            return Number.isFinite(n) ? Math.trunc(n) : value;
        }
        case 'Float': {
            const n = typeof value === 'number' ? value : Number(value);
            return Number.isFinite(n) ? n : value;
        }
        case 'Boolean':
            if (typeof value === 'boolean') return value;
            if (value === 'true' || value === 1) return true;
            if (value === 'false' || value === 0) return false;
            return Boolean(value);
        default:
            return String(value);
    }
}

function gqlTypeOf(column: Column | undefined): string {
    return column?.paramType?.replace('!', '') ?? 'String';
}

export function buildPkEqFilter(
    row: Record<string, unknown> | null | undefined,
    table: Pick<Table, 'primaryKeys' | 'columns'>,
): PkEqFilterResult | null {
    const filter = pkFilterFor(row, table);
    if (!filter) return null;

    const columnByName = new Map((table.columns ?? []).map((c) => [c.name, c] as const));
    const clauses: string[] = [];
    const variables: Record<string, unknown> = {};
    const params: string[] = [];

    for (const pk of table.primaryKeys ?? []) {
        const gqlType = gqlTypeOf(columnByName.get(pk));
        const varName = `pk_${pk}`;
        variables[varName] = coerceForGql(filter[pk], gqlType);
        params.push(`$${varName}: ${gqlType}`);
        clauses.push(`{${pk}: {_eq: $${varName}}}`);
    }

    const filterText = clauses.length === 1 ? clauses[0] : `{and: [${clauses.join(', ')}]}`;
    return { filterText, variables, params };
}

export function encodePkRoute(filter: PkFilter, table: TableLike): string {
    const keys = table.primaryKeys ?? [];
    if (keys.length === 0) return '';
    return encodeRouteParts(keys.map((pk) => filter[pk]));
}

export function parsePkRoute(raw: string, table: TableLike): PkFilter | null {
    const keys = table.primaryKeys ?? [];
    if (keys.length === 0) return null;
    if (keys.length === 1) {
        return { [keys[0]]: decodePkPart(raw) };
    }
    const parts = raw.split(DELIMITER);
    if (parts.length !== keys.length) return null;
    const filter: PkFilter = {};
    for (let i = 0; i < keys.length; i++) {
        filter[keys[i]] = decodePkPart(parts[i]);
    }
    return filter;
}
