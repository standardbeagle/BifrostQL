---
paths:
  - "src/BifrostQL.UI/frontend/**"
  - "examples/**"
  - "packages/**"
  - "samples/HostedSpa/spa/**"
---

# Client rules

- The shipped chain is `src/BifrostQL.UI/frontend` → `@standardbeagle/edit-db`. `@bifrostql/react` and `@bifrostql/app-shell` are experimental. The three fetch clients (`graphql-client.ts`, edit-db `fetcher.ts`, UI `HttpTransport`) are known duplicates; don't merge them ad hoc. Unification targets `QueryTransport` in `frontend/src/lib/transport.ts`.
- `@bifrostql/types` + `@bifrostql/react` publish as a matched pair. Breaking changes go under `### Breaking changes` in the react CHANGELOG.
- Editor data paths go through `useFetcher()`; the transport toggle routes them via `TransportGraphQLFetcher`. Don't bump `editorKey` on a profile change.
- New state in `use-bifrost-table` goes in a focused hook under `hooks/internal/`.
- GraphQL text: never interpolate user-provided names. Filter input type names come from `tableFilterTypeName(table)`; sort enums are `<GraphQlName>SortEnum`. Composite keys: `.claude/rules/composite-pk-compliance.md`.
