/**
 * One place that classifies a column's GraphQL scalar (`paramType`).
 *
 * The type mappers can emit Int, Short, Byte, BigInt, Decimal, Float, Boolean,
 * DateTime, DateTimeOffset, JSON and String. Every surface that reasoned about
 * types used to carry its own `['Int', 'Float']` list, so the four less common
 * numeric scalars and the second timestamp scalar were each handled by whichever
 * surfaces happened to remember them: a smallint column could not be filtered from
 * the grid header, was not validated as a number in a form, and was sent as a
 * string on write (which its `Short!` input rejects). Classifying in one module is
 * what keeps those surfaces from drifting apart again.
 */

/** Strips the non-null marker: `Int!` -> `Int`. */
export function baseParamType(paramType: string | undefined): string {
    return (paramType ?? '').replace('!', '');
}

const INTEGER_SCALARS = new Set(['Int', 'Short', 'Byte', 'BigInt']);
const FRACTIONAL_SCALARS = new Set(['Float', 'Decimal']);
const TIMESTAMP_SCALARS = new Set(['DateTime', 'DateTimeOffset']);

/**
 * Scalars whose value must stay a decimal STRING end to end.
 *
 * A JS number is an IEEE-754 double: it rounds a bigint past 2^53
 * (9007199254740993 becomes ...992, so a key targets a different row) and drops
 * exact-decimal precision. The server's BigInt/Decimal scalars accept the text
 * form for exactly this reason — see ExactNumericScalars.cs.
 */
const EXACT_SCALARS = new Set(['BigInt', 'Decimal']);

/** Whole-number columns — they reject a fractional value. */
export function isIntegerScalar(paramType: string | undefined): boolean {
    return INTEGER_SCALARS.has(baseParamType(paramType));
}

/** Any numeric column, whole or fractional. */
export function isNumericScalar(paramType: string | undefined): boolean {
    const base = baseParamType(paramType);
    return INTEGER_SCALARS.has(base) || FRACTIONAL_SCALARS.has(base);
}

/** Columns whose value must never be routed through a JS number. */
export function isExactScalar(paramType: string | undefined): boolean {
    return EXACT_SCALARS.has(baseParamType(paramType));
}

/** DateTime and DateTimeOffset — the columns whose bounds are instants. */
export function isTimestampScalar(paramType: string | undefined): boolean {
    return TIMESTAMP_SCALARS.has(baseParamType(paramType));
}

export function isBooleanScalar(paramType: string | undefined): boolean {
    return baseParamType(paramType) === 'Boolean';
}

export function isStringScalar(paramType: string | undefined): boolean {
    return baseParamType(paramType) === 'String';
}

/**
 * Validates numeric text and returns the value to send: the digits unchanged for
 * an exact column, otherwise a number. Null means "not (yet) a complete numeric
 * literal", which callers read as "no value", never as zero.
 */
export function parseScalarNumber(raw: string, paramType: string | undefined): number | string | null {
    const text = raw.trim();
    if (text === '' || text === '-' || text === '+') return null;
    const valid = isIntegerScalar(paramType)
        ? /^[+-]?\d+$/.test(text)
        : /^[+-]?(?:(?:\d+\.?\d*)|(?:\.\d+))(?:[eE][+-]?\d+)?$/.test(text);
    if (!valid) return null;
    if (isExactScalar(paramType)) return text;
    const n = Number(text);
    return Number.isFinite(n) ? n : null;
}
