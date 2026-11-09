---
title: "React Hooks for a GraphQL Database API"
description: "Query and mutate a BifrostQL API from React with typed hooks for reads, writes, infinite scroll, and subscriptions, plus a headless TanStack Query table."
---

`@bifrostql/react` is an **experimental** React client for BifrostQL APIs. It is not the client used by the shipped editor — the desktop app's editor is built on `@standardbeagle/edit-db`, which has its own data layer. The companion `@bifrostql/app-shell` package is experimental too, and currently has no importers. Use `@bifrostql/react` for standalone experiments; expect its API to change. It's built on [TanStack Query](https://tanstack.com/query), so you get caching, background refetching, and optimistic updates for free — you just describe the table, fields, filter, and sort.

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
| `useBifrostBatch` | Many writes through `<table>_batch`: one transaction per document, dependency-ordered |
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
- **`useBifrostBatch`** sends many operations through the server's [`<table>_batch` field](/guides/mutations/#batch-mutations), sorted into insert → upsert → update → delete order. Each document is one transaction and takes the [set-based fast path](/reference/bulk-batch-performance/#when-the-fast-path-runs) when the table is eligible. See [Batch transactions and limits](#batch-transactions-and-limits).

### Batch transactions and limits

```tsx
import { useBifrostBatch } from '@bifrostql/react';

const batch = useBifrostBatch({ invalidateQueries: ['users'] });

// One table, one document, one transaction: all three apply or none do.
const { results } = await batch.mutateAsync([
  { type: 'insert', table: 'users', data: { name: 'Bob' } },
  { type: 'update', table: 'users', id: 1, data: { name: 'Alice' } },
  { type: 'delete', table: 'users', id: 99 },
]);
// results: [{ table: 'users', indices: [0, 1, 2], affected: 3 }]
```

- **Operations on one table share one transaction**, unless operations on another table have to run between them. Inserts on `users` and `orders`, then deletes on `orders` and `users`, send three documents: `users` inserts, `orders` inserts and deletes, `users` deletes.
- **BifrostQL has no transaction across tables for this.** A batch over several tables is several transactions, sent one after another. If a later document fails, earlier ones stay committed. For one transaction over rows linked by foreign keys, write a [`save` graph mutation](/guides/mutations/#explicit-ops-graph-saves-save) with `useBifrostMutation`.
- **No partial success inside a document.** The batch stops at the first failed document and rejects with a `BatchError`: `results` lists the documents that committed, `failed` holds the table, the operations' original `indices` and the `error`, and `notAttempted` lists the original indices never sent. The server reports no failing row index. A lost connection cannot tell whether the document committed.
- **`affected` counts rows, not operations.** It is the only per-document result the server returns. An update or delete that matches no row adds 0 and does not fail, and no inserted keys come back. If you need a generated key, insert that row with `useBifrostMutation`.
- **`onProgress` moves a document at a time.** `completed` and `failed` count operations, but a document settles whole, so a single-table batch goes from none to all in one step. `documents` and `documentsSettled` give the document counts.
- **Server limits apply per document.** A document over the table's `batch-max-size` (default 100) is refused whole. The hook does not split it, because that would make it several transactions, so raise `batch-max-size` on tables that take bulk writes. `batch-duplicate-policy` applies too: several updates or deletes of one key collapse to their net effect or are refused, and an upsert sharing a key with an update, delete or another upsert is always refused. That includes a delete-then-upsert replace; use delete-then-insert.
- **Order is yours within a verb.** Tables run in the order you first mention them for that verb, so list parents first for inserts and children first for deletes. A delete followed by an insert or upsert on the same table keeps your order across the whole batch, so the hook doesn't invert the replace, and the server applies the document in that order on both its paths, so `[insert x, delete y, insert y]` works.
- **`invalidateQueries` fires once anything has committed**, including when a later document fails.

### Telling errors apart

When the server refuses a request, hooks reject with a `BifrostGraphQLError`. Read its `code`, the server's `extensions.code`, rather than matching message text:

```tsx
import { BatchError, BifrostGraphQLError, useBifrostMutation } from '@bifrostql/react';

const stopTimer = useBifrostMutation(STOP_TIMER, {
  onError: (error) => {
    if (error instanceof BifrostGraphQLError && error.code === 'CONFLICT') {
      showNotice('Someone else changed this entry. Reload and try again.');
    }
  },
});

// A batch rejects with a BatchError, which carries the same code.
batch.mutateAsync(operations).catch((error) => {
  if (error instanceof BatchError && error.code === 'ACCESS_DENIED') showDenied();
});
```

- `code` is the first error's `extensions.code` that has one. `ACCESS_DENIED`, `CONFLICT` and `PENDING_APPROVAL` come from the engine, and a [server operation](/BifrostQL/guides/server-operations/) can raise its own. An error the engine gives no code to has `BIFROST_EXECUTION_ERROR`.
- `errors` is every error the server sent, unchanged, with `message`, `path`, `locations` and `extensions`. `message` is their messages joined, as it was before.
- The same error reaches a hook's `onError`, its `error` result (`useBifrost`, `useBifrostQuery`, `useBifrostInfinite`, `useBifrostMutation`, `useBifrostSubscription`) and `executeGraphQL`. On a `BatchError` it is `failed.error`.

Three failures carry no `code`:

- **`BifrostAuthError`** takes the [session flow](/BifrostQL/guides/react-native/) and has no `code`. An HTTP 401 is one, and so is a GraphQL error whose `extensions.code` is `UNAUTHENTICATED` or `UNAUTHORIZED`. A GraphQL error with any other code is a `BifrostGraphQLError`, whatever its message says, so a server operation's `NOT_TIMER_OWNER` refusal reading "You are unauthorized…" never logs anyone out. Only an error with no code is judged by its message, `unauthorized` or `unauthenticated`. BifrostQL's own errors always carry a code on the wire (`BIFROST_EXECUTION_ERROR` when nothing named one), so a server operation that means "sign in again" has to set `ErrorCode = "UNAUTHENTICATED"`.
- **A non-2xx response** is a plain `Error` naming the status. The server's request-level refusals, such as an HTTP 400 or 403 from the endpoint itself, land here.
- **Errors the client raises itself**, such as `useBifrostDiff`'s conflict error, are plain `Error`s.

### Headless tables

`useBifrostTable` is a complete table engine without any markup — sorting, multi-column filtering, pagination, row selection, expandable rows, column show/hide and reorder, optional `urlSync`, client `computed` columns, and built-in `count`/`sum`/`avg`/`min`/`max` aggregates. Bring your own UI, or drop in the pre-built `BifrostTable` component (theming, inline editing, CSV export, row actions).

#### Editing follows the server's policy

Both `useBifrostTable` and `BifrostTable` read the table's `_dbSchema` projection for the caller (the same document `usePolicy` reads, fetched once per `identity` and shared through the query cache) and shape themselves from it:

- `editable` defaults to `'auto'`: the table takes edits only when the projection's `allowedActions` includes `update` **and** a write handler is wired (`onRowUpdate` on the component; `onRowUpdate` or `onBatchSave` on the hook). Pass `true` to assert editing — the component still throws without `onRowUpdate` — or `false` to switch it off; `false` wins over any projection.
- A column takes an editor only when the projection marks it `writable`. A column config's `editable: true` opts a column in, but cannot grant what the server withholds.
- A column projected `readable: false` renders its cell as `—`, so a withheld value never reads as a null.
- A row carrying `_can { update delete }` follows its own answer; a row without it inherits the table's. Row actions declare the capability they need with `permission: 'update' | 'delete'` and render only where it holds; an action without `permission` always renders.
- If the policy document fails to load, the table renders read-only and `BifrostTable` shows the error above the rows. The hook exposes the projection as `policy`, plus `rowCan(row, action)` and `isColumnMasked(field)` for a custom UI.

```tsx
<BifrostTable
  table="members"
  columns={columns}
  identity={session.userId}
  onRowUpdate={(row, changes) => update.mutateAsync({ id: row.id, ...changes })}
  rowActions={[
    { label: 'Delete', permission: 'delete', onClick: remove },
    { label: 'View', onClick: open },
  ]}
/>
```

## Want a whole admin UI instead of hooks?

If you don't want to assemble screens by hand, the [Embeddable Data Editor](/BifrostQL/guides/embedded-editor/) gives you a complete, schema-driven CRUD navigator as a single `<Editor>` component.
