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
