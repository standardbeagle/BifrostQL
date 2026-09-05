import type { PkFilter } from './row-id';

/** Values long enough to be prose rather than a name are not identifying. */
const MAX_DESCRIPTOR_LENGTH = 40;

export interface DeleteSummary {
    /** One human-readable line per row, capped at `maxLabels`. */
    labels: string[];
    /** Rows beyond the cap, for an "and N more" line. */
    remaining: number;
}

const matchesPk = (row: Record<string, unknown>, pk: PkFilter) =>
    Object.entries(pk).every(([column, value]) => String(row[column]) === String(value));

/**
 * Picks the columns that say *which* record this is. A short non-empty string
 * that is not part of the key identifies a row to a person — a name, a number,
 * a location. Long values are prose (notes, descriptions) and identify nothing
 * at a glance, so they are skipped.
 */
const descriptorsOf = (row: Record<string, unknown>, pk: PkFilter, limit: number) =>
    Object.entries(row)
        .filter(([column, value]) =>
            !(column in pk) &&
            typeof value === 'string' &&
            value.trim().length > 0 &&
            value.trim().length <= MAX_DESCRIPTOR_LENGTH)
        .slice(0, limit)
        .map(([, value]) => (value as string).trim());

/**
 * Describes the rows a delete confirmation is about, so the dialog can name
 * them instead of asking the reader to trust that the right row was selected.
 *
 * Falls back to the key alone when the row is not on the current page — a
 * confirmation that names the key is still better than one that names nothing.
 */
export function describeDeleteRows(
    pks: readonly PkFilter[],
    rows: readonly Record<string, unknown>[],
    options: { maxLabels?: number; maxDescriptors?: number } = {},
): DeleteSummary {
    const { maxLabels = 5, maxDescriptors = 2 } = options;

    const labels = pks.slice(0, maxLabels).map((pk) => {
        const key = Object.values(pk).map(String).join(', ');
        const row = rows.find((candidate) => matchesPk(candidate, pk));
        if (!row) return key;
        const descriptors = descriptorsOf(row, pk, maxDescriptors);
        return descriptors.length > 0 ? `${key} — ${descriptors.join(' ')}` : key;
    });

    return { labels, remaining: Math.max(0, pks.length - labels.length) };
}
