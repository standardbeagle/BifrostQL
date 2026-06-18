/**
 * Renders a muted placeholder so empty grid/detail cells are visibly distinct
 * from cells that merely failed to render. In a DB editor the difference between
 * a missing value and a blank string is meaningful, so the two are labelled
 * differently:
 *   - `null` / `undefined`  -> `NULL`
 *   - empty string `""`     -> `empty`
 */
export function EmptyValue({ kind }: { kind: 'null' | 'empty' }) {
    return (
        <span className="text-muted-foreground/60 italic select-none">
            {kind === 'null' ? 'NULL' : 'empty'}
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
