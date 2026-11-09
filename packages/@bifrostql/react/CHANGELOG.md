# Changelog

All notable changes to this project will be documented in this file.

While the major version is 0, breaking changes may land in any release, but
never silently: each one is listed under a `### Breaking changes` heading with
the call-site update it requires.

## 0.4.0 — 2026-11-09

### Breaking changes

- **A coded GraphQL error is judged by its code, not its message.** An error whose
  `extensions.code` is anything but `UNAUTHENTICATED` or `UNAUTHORIZED` is now a
  `BifrostGraphQLError`, even when its message says "unauthorized", and no longer
  calls `refreshToken` or `onSessionExpired`. A `BifrostExecutionError` thrown on the
  server always reaches the client with a code (`BIFROST_EXECUTION_ERROR` when it
  names none), so a server operation that relied on the word "unauthorized" to start
  the session flow must set `ErrorCode = "UNAUTHENTICATED"`. An `UNAUTHENTICATED` or
  `UNAUTHORIZED` code now starts that flow whatever the message says. An error with
  no code is judged by its message, as before.
- **`useBifrostBatch` writes through the server's `<table>_batch` field.** It
  used to send one mutation per operation, with no transaction and none of the
  server's set-based fast path. Each document is now one transaction, so a failure
  rolls back that whole document. All of a table's operations share a document
  unless operations on another table have to run between them. BifrostQL has no
  transaction across tables for this, so a batch over several tables is several
  transactions, and a later failure does not undo an earlier document.
- **`allowPartialSuccess` is removed.** A transaction has no partial success.
  Call `mutate` once per table if each table should succeed or fail on its own.
  There is no runtime check: untyped code that still passes it gets a rejected
  `BatchError` where it used to get a resolved result with `errors`.
- **`BatchResult` changed.** `results` holds one
  `{ table, indices, affected }` per document, replacing one `{ index, data }`
  per operation, and `errors` is gone because a resolved batch has no failures.
  `affected` counts rows across the document and is the only thing the server
  returns, so inserted keys and per-operation data are no longer available. Use
  `useBifrostMutation` for an insert whose generated key you need.
- **`BatchError` changed.** `results` holds the documents that committed, and
  `errors` is replaced by `failed: { table, indices, error }` for the one
  document that failed, plus `notAttempted`, the original indices never sent. The
  server reports no failing row index, so read `failed.indices` as the whole
  document, not as the culprit.
- **`BatchProgress` changed.** `current` is replaced by `documents` and
  `documentsSettled`. `completed` and `failed` still count operations but move a
  document at a time, and `onProgress` fires at the start and once per document.
- **Batches over the table's `batch-max-size` (default 100) are refused whole.**
  The hook does not split them, because that would make them several
  transactions. Raise `batch-max-size` on tables that take bulk writes.
- **The server's `batch-duplicate-policy` now applies.** Several updates or
  deletes of one key collapse to their net effect (`last-wins`, the default) or
  are refused (`reject`). An upsert sharing a key with an update, delete or another
  upsert is always refused, which includes a delete-then-upsert replace: use
  delete-then-insert.
- **An operation that cannot be built refuses the whole batch before anything is
  sent**, with a plain `Error` naming its index. That covers a missing row
  identity (previously a `BatchError` after the operations ahead of it had been
  written) and an insert with no columns, which the server would skip silently.

### Added

- **`BifrostGraphQLError` keeps the server's error code.** A GraphQL `errors` reply
  now rejects with this `Error` subclass, carrying `code` (the first
  `extensions.code`) and `errors` (the whole list as the server sent it), so a
  client can tell a conflict from a denial without matching message text. `message`
  is unchanged. `error.name` is now `BifrostGraphQLError`. It reaches `onError` and
  `error` on the hooks, and the subscription transports. `BatchError` gains `code`
  as well. `BifrostAuthError` and HTTP failures are unchanged and have no `code`.

## 0.3.0 — 2026-09-01

### Breaking changes

- **Built queries now emit BifrostQL's paged `data{}` envelope.**
  `buildGraphqlQuery` produces `{ users(...) { total data { id name } } }`
  instead of a flat field selection — the server's table fields are typed as
  the paged envelope, so the 0.2.0 flat form was rejected by every real
  BifrostQL server (it only ever worked against flat test mocks).
  `useBifrostQuery`/`useBifrostTable` unwrap the envelope, so their `data`
  is still the row array; code consuming `fetchBifrostQuery`'s hydrated
  cache or `createCrudHelpers` result types now sees `PagedResult<TRow>`
  (`{ data, total, offset, limit }`) instead of a bare row array.
- `useBifrostQuery` results carry `total` and `UseBifrostTableResult` carries
  `totalRows` — the envelope's unpaged match count (additive, listed here
  because they change the result object shape).

### Fixed

- The library build externalizes `react/jsx-runtime`/`react/jsx-dev-runtime`.
  0.2.0's dist inlined a JSX runtime compiled against React 18 internals and
  crashed React 19 hosts at import time (`ReactCurrentDispatcher`).
- `invalidateQueries: ['users']` (table-name form) on `useBifrostMutation`,
  `useBifrostDiff`, and `useBifrostBatch` now actually invalidates. Queries
  are keyed by full query string, so the table-name form used to build a key
  prefix that matched nothing — a silent no-op. Table names now match any
  cached bifrost query whose text references the table; full-query-string
  entries keep exact-prefix matching.

## 0.2.0 — 2026-08-31 (first npm release)

### Breaking changes

- `QueryOptions.fields` and `QueryOptions.sort` (in `@bifrostql/types`, re-exported
  here) are now `readonly` arrays. Passing a mutable array still compiles;
  only code that mutated an options object's arrays in place needs a copy.
- `SortOption` lost no members, but sort entries on the typed option surfaces
  (`UseBifrostQueryOptions`, `UseBifrostTableOptions`, `BifrostTableProps`)
  are now `SortOptionFor<TRow>`, whose `field` is `keyof TRow` when a concrete
  row type argument is supplied. Untyped call sites are unaffected.

Naming cleanup across the table API. This package is experimental and has no
external consumers, so the old names were renamed rather than aliased — the
option objects are structurally typed, so a stale `query`/`defaultFilters` key
is silently ignored rather than reported as an error. Update call sites:

- `UseBifrostTableOptions.query` and `BifrostTableProps.query` are now `table`.
  The value was always a table name, never a GraphQL document; `useBifrostQuery`
  already called it `table`. `useBifrost(query)`, which does take raw GraphQL,
  is unchanged.
- `ChildQueryConfig.query` is now `ChildQueryConfig.table`, for the same reason.
- `defaultFilters` is now `defaultFilter` — the option is a single `TableFilter`,
  and the matching query option is `filter`.
- `useBifrostTable(...)` returns `search` instead of `performance`, and the type
  `PerformanceState` is now `SearchState`. The state is debounced search input;
  request metrics were the secondary concern.
- `TableExportFormat` is now `ExportMenuFormat`, to distinguish the export
  menu's `'csv' | 'json'` from the hook's wider `ExportFormat`.

### Fixed

- `BifrostTable`'s CSV export now shares `utils/table-export`, which quotes
  header cells and values containing newlines or carriage returns. The previous
  inline implementation escaped only commas and quotes, so a cell containing a
  newline corrupted the row structure of the exported file.

### Added

- Typed field selection: `useBifrostQuery<User[]>` and `useBifrostTable<User>`
  now constrain `fields` and `sort[].field` to `keyof User`; a misspelled
  field name is a compile error. Supplying no row type keeps the previous
  plain-string behavior. `FieldNameOf<TRow>` and `SortOptionFor<TRow>` are
  exported from `@bifrostql/types` and re-exported here.
- An npm publish workflow (`bifrostql-npm-publish`) publishes
  `@bifrostql/types` and `@bifrostql/react` as a matched version pair, with
  the `workspace:*` dependency rewritten to the concrete version at pack time.
- `UseBifrostQueryOptions` is now exported, as every sibling hook's options type
  already was.
- `VirtualScrollConfig`, `VirtualScrollState`, `VisibleRange`, and `SearchState`
  are exported from the package root; they appear in `UseBifrostTableResult` but
  consumers previously could not name them.

## 0.1.0 - 2026-02-14

Initial release of `@bifrostql/react`.

### Components

- `BifrostProvider` - context provider for BifrostQL configuration (endpoint, headers)
- `BifrostTable` - pre-built table component with theming, inline editing, CSV export, row actions, sorting, filtering, and pagination

### Hooks

- `useBifrost` - low-level GraphQL query hook with TanStack Query integration, retry with exponential backoff, and query invalidation
- `useBifrostQuery` - table-oriented query hook with declarative filter, sort, and pagination options
- `useBifrostMutation` - mutation hook with automatic query invalidation
- `useBifrostInfinite` - infinite scroll / cursor-based pagination hook
- `useBifrostSubscription` - real-time data via WebSocket (graphql-transport-ws) or Server-Sent Events with automatic reconnection
- `useBifrostDiff` - diff-based update mutations that send only changed fields, with three-way conflict detection
- `useBifrostBatch` - sequential batch mutation execution with progress tracking and dependency-ordered operations
- `useBifrostTable` - headless table state management (sorting, filtering, pagination, row selection, column visibility/reorder, URL sync, computed columns, aggregates)

### Server-Side Rendering (`@bifrostql/react/server`)

- `getQueryClient` - singleton QueryClient for server-side prefetching
- `resetServerQueryClient` - reset the server singleton between requests
- `fetchBifrostQuery` - prefetch BifrostQL queries into a QueryClient for hydration
- `parseTableParams` - parse URL search parameters into QueryOptions (Directus-style filter syntax)

### Utilities

- `buildGraphqlQuery` - construct GraphQL query strings from table name and options
- `executeGraphQL` - execute GraphQL requests via fetch
- `buildMutation` / `buildInsertMutation` / `buildUpdateMutation` / `buildUpsertMutation` / `buildDeleteMutation` - mutation string builders
- `diff` / `detectConflicts` - object diffing and three-way conflict detection
- `serializeSort` / `parseSort` / `serializeFilter` / `parseFilter` - state serialization for URL persistence
- `writeToUrl` / `readFromUrl` - URL search parameter state management

### Theming

- Four built-in table themes: `modern`, `classic`, `minimal`, `dense`
- `getTheme` utility for accessing theme objects
- Theme override support via `themeOverrides` prop on `BifrostTable`
