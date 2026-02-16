# Changelog

All notable changes to this project will be documented in this file.

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
