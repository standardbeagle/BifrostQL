---
title: React Hooks & Components
description: Query, mutate, and stream a BifrostQL API from React with @bifrostql/react — typed hooks for queries, mutations, infinite scroll, subscriptions, diff/batch writes, and a headless table built on TanStack Query.
---

`@bifrostql/react` is an **experimental** React client for BifrostQL APIs. It is not the client used by the shipped editor — the desktop app's editor is built on `@standardbeagle/edit-db`, which has its own data layer. Use `@bifrostql/react` for standalone experiments; expect its API to change. It's built on [TanStack Query](https://tanstack.com/query), so you get caching, background refetching, and optimistic updates for free — you just describe the table, fields, filter, and sort.

## Install

```bash
npm install @bifrostql/react @tanstack/react-query react react-dom
```

Wrap your app once with the TanStack `QueryClientProvider` and `BifrostProvider`:

```tsx
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { BifrostProvider } from '@bifrostql/react';

const queryClient = new QueryClient();

export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <BifrostProvider config={{ endpoint: 'http://localhost:5000/graphql' }}>
        <UserList />
      </BifrostProvider>
    </QueryClientProvider>
  );
}
```

## Querying

`useBifrostQuery` is the table-oriented hook — pass the table name and what you want back:

```tsx
import { useBifrostQuery } from '@bifrostql/react';

function UserList() {
  const { data, isLoading, error } = useBifrostQuery('users', {
    fields: ['id', 'name', 'email'],
    filter: { active: true, age: { _gte: 18 }, role: { _in: ['admin', 'editor'] } },
    sort: [{ field: 'name', direction: 'asc' }],
    pagination: { limit: 25 },
  });

  if (isLoading) return <div>Loading…</div>;
  if (error) return <div>{error.message}</div>;
  return <ul>{data?.map((u) => <li key={u.id}>{u.name}</li>)}</ul>;
}
```

Filter operators mirror the API: `_eq`, `_neq`, `_gt`, `_gte`, `_lt`, `_lte`, `_in`, `_nin`, `_contains`, `_ncontains`, `_starts_with`, `_ends_with`, `_null`, `_nnull`.

## The hook toolbox

| Hook | Use it for |
|------|-----------|
| `useBifrost` | Low-level raw GraphQL — everything else builds on it |
| `useBifrostQuery` | Table reads with filter / sort / pagination |
| `useBifrostMutation` | Insert / update / upsert / delete with auto-invalidation |
| `useBifrostInfinite` | Infinite scroll (offset or cursor) |
| `useBifrostSubscription` | Real-time via `graphql-transport-ws` WebSocket **or** SSE (`transport: 'auto'`) |
| `useBifrostDiff` | Send only changed fields, with conflict detection |
| `useBifrostBatch` | Sequential multi-mutation writes with progress, dependency-ordered |
| `useBifrostTable` | Headless table state: sort, filter, paginate, select, columns, URL sync, aggregates |

### Mutations

Mutation builders generate the right GraphQL for each operation and invalidation keeps the cache fresh:

```tsx
import { useBifrostMutation, buildInsertMutation } from '@bifrostql/react';

const insert = useBifrostMutation(buildInsertMutation('users'), {
  invalidateQueries: ['users'],
});
insert.mutate({ detail: { name: 'Alice', email: 'alice@example.com' } });
```

`buildInsertMutation`, `buildUpdateMutation`, `buildUpsertMutation`, and `buildDeleteMutation` cover the four mutation shapes.

### Efficient writes

- **`useBifrostDiff`** previews and submits only the fields that actually changed, and flags conflicts if the server row drifted since you loaded it.
- **`useBifrostBatch`** runs many operations in one go, sorted into insert → update → delete order, with `onProgress` reporting and optional `allowPartialSuccess`.

### Headless tables

`useBifrostTable` is a complete table engine without any markup — sorting, multi-column filtering, pagination, row selection, expandable rows, column show/hide and reorder, optional `urlSync`, client `computed` columns, and built-in `count`/`sum`/`avg`/`min`/`max` aggregates. Bring your own UI, or drop in the pre-built `BifrostTable` component (theming, inline editing, CSV export, row actions).

## Want a whole admin UI instead of hooks?

If you don't want to assemble screens by hand, the [Embeddable Data Editor](/BifrostQL/guides/embedded-editor/) gives you a complete, schema-driven CRUD navigator as a single `<Editor>` component.
