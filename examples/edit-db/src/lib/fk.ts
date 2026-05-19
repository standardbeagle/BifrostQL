import type { Column, Join, Table } from '../types/schema';

export interface FkEqFilterResult {
    filterText: string;
    variables: Record<string, unknown>;
    params: string[];
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

export function isFkMember(join: Join, columnName: string): boolean {
    return (join.sourceColumnNames ?? []).includes(columnName);
}

export function isComposite(join: Join): boolean {
    return (join.sourceColumnNames?.length ?? 0) > 1;
}

export function findJoinBySource(joins: Join[], columnName: string): Join | undefined {
    return joins.find((j) => isFkMember(j, columnName));
}

export function findJoinByDestinationTable(joins: Join[], destinationTable: string): Join | undefined {
    return joins.find((j) => j.destinationTable === destinationTable);
}

/**
 * Build a `{_eq}`-per-column composite filter against the destination side of a join,
 * using values pulled from a source-side row. Single-column joins emit a single
 * `{col: {_eq: $var}}` clause; composite joins are wrapped in `{and: [...]}`.
 */
export function buildFkEqFilter(
    sourceRow: Record<string, unknown> | null | undefined,
    join: Join,
    destinationTable: Pick<Table, 'columns'> | undefined,
    varPrefix = 'fk',
): FkEqFilterResult | null {
    if (!sourceRow) return null;
    const sourceCols = join.sourceColumnNames ?? [];
    const destCols = join.destinationColumnNames ?? [];
    if (sourceCols.length === 0 || sourceCols.length !== destCols.length) return null;

    const columnByName = new Map((destinationTable?.columns ?? []).map((c) => [c.name, c] as const));
    const clauses: string[] = [];
    const variables: Record<string, unknown> = {};
    const params: string[] = [];

    for (let i = 0; i < sourceCols.length; i++) {
        const srcCol = sourceCols[i];
        const dstCol = destCols[i];
        if (!(srcCol in sourceRow)) return null;
        const gqlType = gqlTypeOf(columnByName.get(dstCol));
        const varName = `${varPrefix}_${dstCol}`;
        variables[varName] = coerceForGql(sourceRow[srcCol], gqlType);
        params.push(`$${varName}: ${gqlType}`);
        clauses.push(`{${dstCol}: {_eq: $${varName}}}`);
    }

    const filterText = clauses.length === 1 ? clauses[0] : `{and: [${clauses.join(', ')}]}`;
    return { filterText, variables, params };
}

/**
 * Extract source-side column values from a row into a plain `{col: value}` object.
 * Returns null if any source column is missing from the row, so callers can short-circuit.
 */
export function fkSourceValues(
    sourceRow: Record<string, unknown> | null | undefined,
    join: Join,
): Record<string, unknown> | null {
    if (!sourceRow) return null;
    const sourceCols = join.sourceColumnNames ?? [];
    if (sourceCols.length === 0) return null;
    const out: Record<string, unknown> = {};
    for (const c of sourceCols) {
        if (!(c in sourceRow)) return null;
        out[c] = sourceRow[c];
    }
    return out;
}
