/**
 * Pure record-cursor math for the form runner's first/prev/next/last navigation.
 *
 * The runner browses a table one record at a time by absolute offset (a
 * `limit: 1 offset: <index>` fetch), so the only stateful arithmetic is moving a
 * bounded index across `[0, total)`. Keeping it here makes it unit-testable
 * without a fetcher or DOM, and keeps the component thin.
 */

export type NavDirection = 'first' | 'prev' | 'next' | 'last';

/**
 * Resolves the target absolute index for a navigation step, clamped into the
 * valid range. `total <= 0` (empty table) always resolves to 0 so callers show
 * the "no records" state rather than a negative offset.
 */
export function nextIndex(current: number, total: number, direction: NavDirection): number {
  const last = Math.max(0, total - 1);
  switch (direction) {
    case 'first':
      return 0;
    case 'last':
      return last;
    case 'prev':
      return Math.max(0, Math.min(last, current - 1));
    case 'next':
      return Math.max(0, Math.min(last, current + 1));
  }
}

/** Whether a navigation step from `current` would actually move (false at a bound). */
export function canNavigate(current: number, total: number, direction: NavDirection): boolean {
  return nextIndex(current, total, direction) !== current;
}

/** Human 1-based position label, e.g. "3 of 12". Empty table reads "0 of 0". */
export function positionLabel(index: number, total: number): string {
  if (total <= 0) return '0 of 0';
  return `${index + 1} of ${total}`;
}
