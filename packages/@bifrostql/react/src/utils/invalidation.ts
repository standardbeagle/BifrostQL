import type { QueryClient, QueryKey } from '@tanstack/react-query';

/**
 * The one producer of bifrost query-cache keys:
 * `['bifrost', endpoint, <full query string>, vars, suffix?]`.
 *
 * The endpoint is part of the key because a `BifrostProvider` reuses a parent
 * `QueryClient`: two providers pointed at different endpoints (tenants) that
 * run the same query text would otherwise serve each other's rows.
 * `useBifrost`, `fetchBifrostQuery` (SSR prefetch) and the invalidation
 * helper below all build keys here so the shapes cannot drift.
 */
export function bifrostQueryKey(
  endpoint: string,
  query: string,
  variables?: Record<string, unknown>,
  suffix?: string,
): QueryKey {
  return suffix === undefined
    ? ['bifrost', endpoint, query, variables ?? {}]
    : ['bifrost', endpoint, query, variables ?? {}, suffix];
}

/**
 * Invalidate cached bifrost queries on `endpoint` for the given entries.
 *
 * Keys come from {@link bifrostQueryKey} (infinite queries share the
 * `['bifrost', endpoint, query]` prefix), so the two entry forms are matched
 * differently:
 *
 * - An entry containing `{` is treated as a full query string and matched as
 *   an exact key prefix.
 * - Anything else is treated as a TABLE NAME and invalidates every bifrost
 *   query on the endpoint whose query text references it as a word. This may
 *   over-invalidate (the name appearing in an unrelated query's text), which
 *   only costs a refetch — under-invalidation would silently show stale rows.
 *
 * Shared by `useBifrostMutation`, `useBifrostDiff`, and `useBifrostBatch` so
 * the matching rules cannot drift between the three write hooks.
 */
export function invalidateBifrostQueries(
  queryClient: QueryClient,
  endpoint: string,
  entries: readonly string[],
): void {
  for (const key of entries) {
    if (key.includes('{')) {
      queryClient.invalidateQueries({ queryKey: ['bifrost', endpoint, key] });
    } else {
      const word = new RegExp(
        `\\b${key.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\b`,
      );
      queryClient.invalidateQueries({
        predicate: (q) =>
          q.queryKey[0] === 'bifrost' &&
          q.queryKey[1] === endpoint &&
          typeof q.queryKey[2] === 'string' &&
          word.test(q.queryKey[2]),
      });
    }
  }
}
