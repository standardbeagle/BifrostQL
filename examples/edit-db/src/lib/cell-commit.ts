/**
 * Whether an inline-editor commit leaves a cell at its stored value, so the
 * grid un-stages it instead of marking the row dirty.
 *
 * The inline editor seeds a NULL cell with '' (a text input cannot hold null)
 * and commits on blur, so tabbing through a NULL cell commits ''. That is the
 * NULL it was shown, not an edit: counting it as one staged '' for every
 * nullable column the user only looked at, and "Save all" wrote it.
 */
export function isUnchangedCellValue(originalValue: unknown, value: unknown): boolean {
    if (originalValue == null) return value == null || value === '';
    if (value == null) return false;
    return String(originalValue) === String(value);
}
