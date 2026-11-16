/**
 * Renders a muted placeholder so empty grid/detail cells are visibly distinct
 * from cells that merely failed to render. In a DB editor the difference between
 * a missing value and a blank string is meaningful, so the two are labelled
 * differently:
 *   - `null` / `undefined`  -> `NULL`
 *   - empty string `""`     -> `empty`
 *   - a placeholder instant (epoch, year 0001) a date column stores to mean
 *     "not set" -> `unset`, with the stored value in `title`
 */
export function EmptyValue({ kind, title }: { kind: 'null' | 'empty' | 'unset'; title?: string }) {
    return (
        <span className="text-muted-foreground/60 italic select-none" title={title}>
            {kind === 'null' ? 'NULL' : kind}
        </span>
    );
}

/**
 * Returns true when `value` should render as an EmptyValue placeholder rather
 * than its stringified form.
 */
export function isEmptyValue(value: unknown): boolean {
    return value === null || value === undefined || value === '';
}
