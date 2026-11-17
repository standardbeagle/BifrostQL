import type { Column, Table } from '../types/schema';
import { coerceForGql, gqlTypeOf } from './fk';

export type PkFilter = Record<string, unknown>;

/**
 * Every primary-key column of `table`, in key declaration order. Composite keys
 * need all of them for a by-id filter and an update's WHERE clause; a key name
 * with no matching column is dropped (the caller's key-count checks catch it).
 */
export function pkColumnsOf(table: Pick<Table, 'primaryKeys' | 'columns'>): Column[] {
    const byName = new Map(table.columns.map((c) => [c.name, c] as const));
    return (table.primaryKeys ?? []).map((pk) => byName.get(pk)).filter((c): c is Column => !!c);
}

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

export function decodePkPart(raw: string): string | null {
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

/**
 * The key-equality predicate and its variable declarations, without values:
 * `{col: {_eq: $pk_col}}` per key column, `and`-wrapped when composite. The one
 * producer of the `pk_<col>` variable names, for query text built before the
 * key values are known; {@link buildPkEqFilter} binds them.
 */
export function pkEqDeclaration(table: TableLike): Pick<PkEqFilterResult, 'filterText' | 'params'> {
    const columnByName = new Map((table.columns ?? []).map((c) => [c.name, c] as const));
    const clauses: string[] = [];
    const params: string[] = [];
    for (const pk of table.primaryKeys ?? []) {
        params.push(`$pk_${pk}: ${gqlTypeOf(columnByName.get(pk))}`);
        clauses.push(`{${pk}: {_eq: $pk_${pk}}}`);
    }
    const filterText = clauses.length === 1 ? clauses[0] : `{and: [${clauses.join(', ')}]}`;
    return { filterText, params };
}

export function buildPkEqFilter(
    row: Record<string, unknown> | null | undefined,
    table: Pick<Table, 'primaryKeys' | 'columns'>,
): PkEqFilterResult | null {
    const filter = pkFilterFor(row, table);
    if (!filter) return null;

    const columnByName = new Map((table.columns ?? []).map((c) => [c.name, c] as const));
    const variables: Record<string, unknown> = {};
    for (const pk of table.primaryKeys ?? []) {
        variables[`pk_${pk}`] = coerceForGql(filter[pk], gqlTypeOf(columnByName.get(pk)));
    }
    return { ...pkEqDeclaration(table), variables };
}

export function encodePkRoute(filter: PkFilter, table: TableLike): string {
    const keys = table.primaryKeys ?? [];
    if (keys.length === 0) return '';
    return encodeRouteParts(keys.map((pk) => filter[pk]));
}

/**
 * Parses a route-encoded id back into a PK filter, or null when the route cannot
 * key a row.
 *
 * An EMPTY segment is a parse failure, not a null key value. Primary-key columns
 * are non-nullable, so `{line_id: null}` can never match a stored row — but it is
 * a structurally valid filter, so callers guarding on "did the key resolve?"
 * (see `pkResolveFailed` in data-edit.tsx) saw success, queried, matched zero
 * rows, and then wrote an all-columns-blank UPDATE back. Returning null here is
 * the established failure shape every caller already handles.
 */
export function parsePkRoute(raw: string, table: TableLike): PkFilter | null {
    const keys = table.primaryKeys ?? [];
    if (keys.length === 0) return null;
    const parts = keys.length === 1 ? [raw] : raw.split(DELIMITER);
    if (parts.length !== keys.length) return null;
    const filter: PkFilter = {};
    for (let i = 0; i < keys.length; i++) {
        const value = decodePkPart(parts[i]);
        if (value === null) return null;
        filter[keys[i]] = value;
    }
    return filter;
}
