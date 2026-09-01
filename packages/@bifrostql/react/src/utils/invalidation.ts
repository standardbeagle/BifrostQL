import type { QueryClient } from '@tanstack/react-query';

/**
 * Invalidate cached bifrost queries for the given entries.
 *
 * Bifrost queries are keyed `['bifrost', <full query string>, vars]`, so the
 * two entry forms are matched differently:
 *
 * - An entry containing `{` is treated as a full query string and matched as
 *   an exact key prefix.
 * - Anything else is treated as a TABLE NAME and invalidates every bifrost
 *   query whose query text references it as a word. This may over-invalidate
 *   (the name appearing in an unrelated query's text), which only costs a
 *   refetch — under-invalidation would silently show stale rows.
 *
 * Shared by `useBifrostMutation`, `useBifrostDiff`, and `useBifrostBatch` so
 * the matching rules cannot drift between the three write hooks.
 */
export function invalidateBifrostQueries(
  queryClient: QueryClient,
  entries: readonly string[],
): void {
  for (const key of entries) {
    if (key.includes('{')) {
      queryClient.invalidateQueries({ queryKey: ['bifrost', key] });
    } else {
      const word = new RegExp(
        `\\b${key.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\b`,
      );
      queryClient.invalidateQueries({
        predicate: (q) =>
          q.queryKey[0] === 'bifrost' &&
          typeof q.queryKey[1] === 'string' &&
          word.test(q.queryKey[1]),
      });
    }
  }
}
