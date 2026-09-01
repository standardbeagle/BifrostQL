# Changelog

All notable changes to this project will be documented in this file.

While the major version is 0, breaking changes may land in any release, but
never silently: each one is listed under a `### Breaking changes` heading with
the call-site update it requires.

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
