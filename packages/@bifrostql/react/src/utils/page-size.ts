/** Page sizes a URL may restore when the table configures no `pageSizeOptions`. */
export const DEFAULT_PAGE_SIZE_OPTIONS: readonly number[] = [10, 25, 50, 100];

/**
 * The page sizes a table accepts from outside its own code (the URL, browser
 * history): the configured `pageSizeOptions`, or the defaults, plus the
 * table's initial page size so its own default always round-trips.
 */
export function resolveAllowedPageSizes(
  pageSizeOptions: readonly number[] | undefined,
  initialPageSize: number,
): readonly number[] {
  const options = pageSizeOptions ?? DEFAULT_PAGE_SIZE_OPTIONS;
  return options.includes(initialPageSize)
    ? options
    : [...options, initialPageSize];
}

/**
 * `requested` when it is an allowed page size, otherwise `fallback`. A page
 * size read from a URL is user input: `?table_size=1000000` must not turn
 * into a million-row request.
 */
export function clampPageSize(
  requested: number | undefined,
  allowed: readonly number[],
  fallback: number,
): number {
  return requested !== undefined && allowed.includes(requested)
    ? requested
    : fallback;
}
