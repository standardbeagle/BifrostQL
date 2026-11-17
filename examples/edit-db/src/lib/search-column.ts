import { baseParamType, isNumericScalar, isStringScalar, parseScalarNumber } from './scalar-types';

/**
 * Which column the header quick-search starts on. The host's configured column
 * for the table wins when it is one of the searchable options; otherwise the
 * first option, so a table with no configuration behaves as before. Returns the
 * option's `value` (the `name,paramType` pair the header keys its picker by).
 */
export function pickDefaultSearchColumn(
    options: ReadonlyArray<{ key: string; value: string }> | undefined,
    configured: string | undefined,
): string {
    if (!options || options.length === 0) return '';
    if (configured) {
        const match = options.find((o) => o.key === configured);
        if (match) return match.value;
    }
    return options[0].value;
}

/**
 * The URL the header quick search navigates to: the current query string with
 * only `filter` replaced. Column filters (`cf`) and grouping (`gb`/`gs`) stay,
 * and URLSearchParams encodes the term, so `&`, `+`, `#` or `%` in it reach the
 * filter intact. Null when the term is not a value of the column's scalar.
 */
export function quickSearchUrl(current: URLSearchParams, columnName: string, paramType: string, term: string): string | null {
    const base = baseParamType(paramType);
    let filter: string;
    if (isNumericScalar(base)) {
        // parseScalarNumber keeps a bigint/decimal term as its digits: through a
        // JS number it would round and search for a different value.
        const parsed = parseScalarNumber(term, base);
        if (parsed === null) return null;
        filter = JSON.stringify([columnName, '_eq', parsed, paramType]);
    } else if (isStringScalar(base)) {
        filter = JSON.stringify([columnName, '_contains', term, paramType]);
    } else {
        return null;
    }
    const params = new URLSearchParams(current.toString());
    params.set('filter', filter);
    return `?${params.toString()}`;
}
