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
import type { QueryTransport } from "./transport";

export class TransportGraphQLFetcher implements GraphQLFetcher {
  constructor(private readonly transport: QueryTransport) {}

  async query<T = unknown>(
    query: string,
    variables?: Record<string, unknown>
  ): Promise<T> {
    const { data, errors } = await this.transport.query(query, variables);
    if ((errors ?? []).length > 0) {
      // The Editor's hooks rely on the fetcher rejecting so react-query can
      // surface the failure; a joined message keeps parity with the built-in
      // HttpGraphQLFetcher's GraphQLRequestError message.
      throw new Error(errors.join("; "));
    }
    return data as T;
  }
}
