/**
 * Adapter that lets the embedded `@standardbeagle/edit-db` Editor issue all of
 * its GraphQL traffic through a pluggable {@link QueryTransport}.
 *
 * The Editor accepts a `fetcher` prop implementing the `GraphQLFetcher`
 * interface (`query<T>(query, variables) => Promise<T>` that resolves with the
 * `data` payload and rejects on GraphQL errors). Our transport layer speaks a
 * slightly different envelope — `{ data, errors }` where `errors` is a string
 * array — so this class bridges the two:
 *
 * - Delegates the actual request to whichever transport (HTTP/JSON or binary
 *   WebSocket) is currently selected.
 * - Collapses a non-empty `errors` array into a thrown `Error` so the Editor's
 *   react-query hooks land in their error state, matching the behavior of the
 *   Editor's built-in `HttpGraphQLFetcher`.
 * - Returns the bare `data` payload cast to the caller's expected type.
 *
 * With this adapter injected as the Editor's `fetcher`, every data path in the
 * Editor (`useSchema`, `useDataTable`, mutation hooks, stats, etc.) routes
 * through the transport, so the header transport toggle actually re-routes
 * editor queries instead of only driving a health probe.
 */

import type { GraphQLFetcher } from "@standardbeagle/edit-db";
import type { QueryTransport, QueryTransportOptions } from "./transport";

/**
 * Rejection shape matching the editor's own `GraphQLRequestError`: same `name`,
 * same `errors` and `data` properties, same joined message. A consumer that
 * inspects the rejection must not degrade just because the transport toggle
 * flipped, and it used to — this adapter threw a bare `Error` carrying only a
 * joined string, so `errors` and any partial `data` vanished on the binary path.
 *
 * The editor's class itself cannot be reused: `@standardbeagle/edit-db` does
 * not re-export `GraphQLRequestError` from its package root, and its `exports`
 * map exposes only `.` and `./style.css`, so there is no importable path to it.
 * That leaves structural parity — enough for `name` checks and for reading
 * `errors`/`data`, but NOT for `instanceof`. Re-exporting the class from
 * edit-db's index would close that last gap; the two client stacks are
 * deliberately separate (see AGENTS.md), so this stops short of merging them.
 */
export class GraphQLRequestError extends Error {
  readonly errors: Array<{ message: string }>;
  readonly data?: unknown;

  constructor(errors: string[], data?: unknown) {
    super(errors.join("; "));
    this.name = "GraphQLRequestError";
    this.errors = errors.map((message) => ({ message }));
    this.data = data;
  }
}

export class TransportGraphQLFetcher implements GraphQLFetcher {
  constructor(private readonly transport: QueryTransport) {}

  // `options` is structurally the package's GraphQLQueryOptions ({ signal? });
  // that type isn't re-exported from the package root, so we reuse the
  // transport's own option shape — the two are interchangeable.
  async query<T = unknown>(
    query: string,
    variables?: Record<string, unknown>,
    options?: QueryTransportOptions
  ): Promise<T> {
    // Forward the abort signal so superseded binary/HTTP requests are cancelled
    // rather than left to run to completion (or to the request timeout).
    const { data, errors } = await this.transport.query(query, variables, options);
    if ((errors ?? []).length > 0) {
      // The Editor's hooks rely on the fetcher rejecting so react-query can
      // surface the failure. Carry the structured errors and any partial data
      // through, matching the built-in HttpGraphQLFetcher's rejection: a
      // GraphQL response can be a partial success, and throwing that data away
      // loses rows the server actually returned.
      throw new GraphQLRequestError(errors, data);
    }
    return data as T;
  }
}
