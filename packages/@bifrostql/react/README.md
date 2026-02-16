# @bifrostql/react

React hooks and components for [BifrostQL](https://github.com/standardbeagle/bifrostql) GraphQL APIs. Built on [TanStack Query](https://tanstack.com/query) for caching, background refetching, and optimistic updates.

## Installation

```bash
npm install @bifrostql/react @tanstack/react-query react react-dom
```

### Peer Dependencies

| Package                 | Version   |
| ----------------------- | --------- |
| `react`                 | >= 18.0.0 |
| `react-dom`             | >= 18.0.0 |
| `@tanstack/react-query` | >= 5.0.0  |

## Quick Start

Wrap your application with `BifrostProvider` and a TanStack Query `QueryClientProvider`:

```tsx
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { BifrostProvider } from '@bifrostql/react';

const queryClient = new QueryClient();

function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <BifrostProvider config={{ endpoint: 'http://localhost:5000/graphql' }}>
        <UserList />
      </BifrostProvider>
    </QueryClientProvider>
  );
}
```

Query data with `useBifrostQuery`:

```tsx
import { useBifrostQuery } from '@bifrostql/react';

function UserList() {
  const { data, isLoading, error } = useBifrostQuery('users', {
    fields: ['id', 'name', 'email'],
    filter: { active: true },
    sort: [{ field: 'name', direction: 'asc' }],
    pagination: { limit: 25 },
  });

  if (isLoading) return <div>Loading...</div>;
  if (error) return <div>Error: {error.message}</div>;

  return (
    <ul>
      {data?.map((user) => (
        <li key={user.id}>{user.name}</li>
      ))}
    </ul>
  );
}
```

## Hooks

### `useBifrost`

Low-level hook for executing raw GraphQL queries. All other query hooks build on this.

```tsx
import { useBifrost } from '@bifrostql/react';

const { data, isLoading, error, invalidate } = useBifrost<MyType>(
  '{ users { id name } }',
  {
    /* variables */
  },
  {
    enabled: true,
    staleTime: 5000,
    gcTime: 300000,
    refetchInterval: false,
    refetchOnWindowFocus: true,
    retry: 3,
    retryDelay: 1000,
  },
);

// Invalidate all queries matching this GraphQL string
invalidate();
```

### `useBifrostQuery`

Table-oriented query hook with built-in filter, sort, and pagination support.

```tsx
import { useBifrostQuery } from '@bifrostql/react';

const { data, isLoading, error, invalidate } = useBifrostQuery<User[]>(
  'users',
  {
    fields: ['id', 'name', 'email', 'created_at'],
    filter: {
      active: true,
      age: { _gte: 18 },
      role: { _in: ['admin', 'editor'] },
    },
    sort: [{ field: 'name', direction: 'asc' }],
    pagination: { limit: 50, offset: 0 },
    // TanStack Query options
    staleTime: 10000,
    refetchOnWindowFocus: 'always',
  },
);
```

**Filter operators:** `_eq`, `_neq`, `_gt`, `_gte`, `_lt`, `_lte`, `_in`, `_nin`, `_contains`, `_ncontains`, `_starts_with`, `_ends_with`, `_null`, `_nnull`

### `useBifrostMutation`

Execute GraphQL mutations with automatic query invalidation.

```tsx
import { useBifrostMutation, buildInsertMutation } from '@bifrostql/react';

const mutation = useBifrostMutation<User, { detail: Partial<User> }>(
  buildInsertMutation('users'),
  {
    invalidateQueries: ['users'],
    onSuccess: (data) => console.log('Created:', data),
    onError: (error) => console.error('Failed:', error),
  },
);

// Execute the mutation
mutation.mutate({ detail: { name: 'Alice', email: 'alice@example.com' } });
```

**Mutation builders:**

| Builder                        | GraphQL Output                                                      |
| ------------------------------ | ------------------------------------------------------------------- |
| `buildInsertMutation('users')` | `mutation Insert($detail: Insert_users) { users(insert: $detail) }` |
| `buildUpdateMutation('users')` | `mutation Update($detail: Update_users) { users(update: $detail) }` |
| `buildUpsertMutation('users')` | `mutation Upsert($detail: Upsert_users) { users(upsert: $detail) }` |
| `buildDeleteMutation('users')` | `mutation Delete($detail: Delete_users) { users(delete: $detail) }` |

### `useBifrostInfinite`

Infinite scrolling with cursor or offset-based pagination.

```tsx
import { useBifrostInfinite } from '@bifrostql/react';

const { data, fetchNextPage, hasNextPage, isFetchingNextPage, invalidate } =
  useBifrostInfinite<{ users: User[] }, number>(
    '{ users(limit: $limit, offset: $offset) { id name } }',
    (pageParam) => ({ limit: 20, offset: pageParam }),
    {
      initialPageParam: 0,
      getNextPageParam: (lastPage, allPages) => {
        if (lastPage.users.length < 20) return undefined;
        return allPages.length * 20;
      },
      maxPages: 10,
    },
  );

// All pages flattened
const allUsers = data?.pages.flatMap((page) => page.users) ?? [];
```

### `useBifrostSubscription`

Real-time data via WebSocket (graphql-transport-ws protocol) or Server-Sent Events.

```tsx
import { useBifrostSubscription } from '@bifrostql/react';

const { data, connectionState, isConnected, error } = useBifrostSubscription<{
  orderUpdated: Order;
}>({
  subscription: 'subscription { orderUpdated { id status total } }',
  variables: {},
  transport: 'auto', // 'websocket' | 'sse' | 'auto'
  enabled: true,
  reconnectAttempts: 5,
  reconnectBaseDelay: 1000, // exponential backoff, max 30s
  onData: (data) => console.log('Update:', data),
  onError: (error) => console.error('Error:', error),
});
```

**Connection states:** `connecting`, `connected`, `disconnected`, `error`

### `useBifrostDiff`

Diff-based mutations that send only changed fields, with conflict detection.

```tsx
import { useBifrostDiff } from '@bifrostql/react';

const { mutate, preview, setLastKnown } = useBifrostDiff({
  table: 'users',
  idField: 'id',
  strategy: 'deep', // 'shallow' | 'deep'
  invalidateQueries: ['users'],
});

// Store server state for conflict detection
setLastKnown(serverRow);

// Preview what would change before submitting
const { diff, conflicts } = preview({
  id: 1,
  original: { name: 'Alice', age: 30 },
  updated: { name: 'Alice', age: 31 },
});
// diff.changed = { age: 31 }, diff.hasChanges = true
// conflicts = [] (or field names if server state diverged)

// Submit only the changed fields
mutate({ id: 1, original: serverRow, updated: editedRow });
```

### `useBifrostBatch`

Execute multiple mutations sequentially with progress tracking. Operations are sorted by dependency order (inserts before updates before deletes).

```tsx
import { useBifrostBatch } from '@bifrostql/react';

const { mutate, getProgress } = useBifrostBatch({
  allowPartialSuccess: true,
  invalidateQueries: ['users', 'orders'],
  onProgress: ({ total, completed, failed, current }) => {
    console.log(`${completed}/${total} complete`);
  },
});

mutate([
  { type: 'insert', table: 'users', data: { name: 'Bob' } },
  { type: 'update', table: 'users', id: 1, data: { name: 'Alice Updated' } },
  { type: 'delete', table: 'users', id: 99 },
  {
    type: 'upsert',
    table: 'users',
    key: { email: 'bob@example.com' },
    data: { name: 'Bob' },
  },
]);
```

When `allowPartialSuccess` is `false` (default), a failed operation throws a `BatchError` containing both `results` and `errors` arrays for inspection.

### `useBifrostTable`

All-in-one headless table state management: sorting, filtering, pagination, row selection, column management, URL sync, computed columns, and aggregates.

```tsx
import { useBifrostTable } from '@bifrostql/react';

const table = useBifrostTable<User>({
  query: 'users',
  columns: [
    { field: 'id', header: 'ID', sortable: true, width: 80 },
    { field: 'name', header: 'Name', sortable: true, filterable: true },
    { field: 'email', header: 'Email', sortable: true },
    {
      field: 'displayName',
      header: 'Display',
      computed: (row) => `${row.name} <${row.email}>`,
    },
  ],
  fields: ['id', 'name', 'email'],
  pagination: { pageSize: 25, pageSizeOptions: [10, 25, 50] },
  defaultSort: [{ field: 'name', direction: 'asc' }],
  defaultFilters: {},
  multiSort: false,
  rowKey: 'id',
  urlSync: true, // or { enabled: true, prefix: 'users', debounceMs: 500 }
  expandable: true,
  aggregates: {
    total: { fn: 'count' },
    avgAge: { field: 'age', fn: 'avg' },
    custom: { fn: (values) => values.length },
  },
});

// Returned state objects:
// table.data           - T[]
// table.columns        - ColumnConfig[]
// table.sorting        - { current, setSorting, toggleSort }
// table.filters        - { current, setFilters, setColumnFilter, clearFilters }
// table.pagination     - { page, pageSize, setPage, setPageSize, nextPage, previousPage }
// table.selection      - { selectedRows, toggleRow, selectAll, clearSelection }
// table.expansion      - { expandedRows, toggleExpand, expandAll, collapseAll }
// table.columnManagement - { visibleColumns, toggleColumn, columnOrder, reorderColumn }
// table.aggregates     - Record<string, unknown>
// table.loading        - boolean
// table.error          - Error | null
// table.refetch        - () => void
```

**Built-in aggregate functions:** `count`, `sum`, `avg`, `min`, `max`

## Components

### `BifrostTable`

A pre-built table component using `useBifrostTable` internally. Supports theming, inline editing, CSV export, row actions, and custom cell rendering.

```tsx
import { BifrostTable } from '@bifrostql/react';
import type { ColumnConfig } from '@bifrostql/react';

const columns: ColumnConfig[] = [
  { field: 'id', header: 'ID', sortable: true },
  { field: 'name', header: 'Name', sortable: true, filterable: true },
  { field: 'email', header: 'Email', sortable: true },
];

function UsersPage() {
  return (
    <BifrostTable
      query="users"
      columns={columns}
      theme="modern" // 'modern' | 'classic' | 'minimal' | 'dense'
      striped
      hoverable
      editable
      exportable
      rowKey="id"
      pagination={{ pageSize: 25 }}
      defaultSort={[{ field: 'name', direction: 'asc' }]}
      urlSync
      onRowClick={(row) => navigate(`/users/${row.id}`)}
      rowActions={[
        { label: 'Edit', onClick: (row) => openEditor(row) },
        { label: 'Delete', onClick: (row) => confirmDelete(row) },
      ]}
      renderCell={(value, row, column) => {
        if (column.field === 'email') {
          return <a href={`mailto:${value}`}>{String(value)}</a>;
        }
        return undefined; // fall back to default rendering
      }}
      renderEmpty={() => <p>No users found.</p>}
      renderError={(error) => <p>Failed to load: {error.message}</p>}
      renderLoading={() => <p>Fetching users...</p>}
    />
  );
}
```

**Themes:** Pass `themeOverrides` to customize individual style properties on any built-in theme.

```tsx
<BifrostTable
  theme="modern"
  themeOverrides={{
    headerCell: { backgroundColor: '#1e293b', color: '#f8fafc' },
    bodyRowHover: { backgroundColor: '#dbeafe' },
  }}
  // ...
/>
```

## Server-Side Rendering

The `@bifrostql/react/server` entry point provides utilities for SSR with Next.js or any server-rendered React framework.

### Next.js App Router

```tsx
// app/users/page.tsx
import { dehydrate, HydrationBoundary } from '@tanstack/react-query';
import { getQueryClient, fetchBifrostQuery } from '@bifrostql/react/server';
import { UserList } from './user-list';

export default async function UsersPage() {
  const queryClient = getQueryClient();

  await fetchBifrostQuery(queryClient, {
    endpoint: process.env.BIFROST_ENDPOINT!,
    headers: { Authorization: `Bearer ${getToken()}` },
    table: 'users',
    fields: ['id', 'name', 'email'],
    sort: [{ field: 'name', direction: 'asc' }],
    pagination: { limit: 50 },
    staleTime: 60000,
  });

  return (
    <HydrationBoundary state={dehydrate(queryClient)}>
      <UserList />
    </HydrationBoundary>
  );
}
```

### Next.js Pages Router

```tsx
// pages/users.tsx
import { dehydrate } from '@tanstack/react-query';
import { getQueryClient, fetchBifrostQuery } from '@bifrostql/react/server';

export async function getServerSideProps() {
  const queryClient = getQueryClient();

  await fetchBifrostQuery(queryClient, {
    endpoint: process.env.BIFROST_ENDPOINT!,
    table: 'users',
    fields: ['id', 'name', 'email'],
  });

  return { props: { dehydratedState: dehydrate(queryClient) } };
}
```

### URL Parameter Parsing

Parse URL search parameters into query options on the server, matching Directus-style filter syntax.

```tsx
import { parseTableParams } from '@bifrostql/react/server';

// URL: /api/users?sort=-created_at,name&limit=25&filter[status]=active&filter[age][gte]=18&fields=id,name,email
const options = parseTableParams(searchParams);
// {
//   sort: [{ field: 'created_at', direction: 'desc' }, { field: 'name', direction: 'asc' }],
//   pagination: { limit: 25 },
//   filter: { status: 'active', age: { _gte: 18 } },
//   fields: ['id', 'name', 'email'],
// }
```

## Utilities

These are also exported from the main entry point for direct use.

| Utility                                                | Purpose                                                  |
| ------------------------------------------------------ | -------------------------------------------------------- |
| `buildGraphqlQuery(table, options)`                    | Build a GraphQL query string from table name and options |
| `executeGraphQL(endpoint, headers, query, variables?)` | Execute a GraphQL request via `fetch`                    |
| `buildMutation(table, type)`                           | Build a mutation string for any operation type           |
| `buildInsertMutation(table)`                           | Shorthand for insert mutations                           |
| `buildUpdateMutation(table)`                           | Shorthand for update mutations                           |
| `buildUpsertMutation(table)`                           | Shorthand for upsert mutations                           |
| `buildDeleteMutation(table)`                           | Shorthand for delete mutations                           |
| `diff(original, updated, strategy?)`                   | Compute changed fields between two objects               |
| `detectConflicts(base, current, incoming)`             | Detect three-way merge conflicts                         |
| `serializeSort(sort)`                                  | Serialize sort state to a URL-safe string                |
| `parseSort(raw)`                                       | Parse a serialized sort string                           |
| `serializeFilter(filter)`                              | Serialize filter state to JSON                           |
| `parseFilter(raw)`                                     | Parse a serialized filter string                         |
| `writeToUrl(state, prefix)`                            | Write table state to URL search parameters               |
| `readFromUrl(prefix)`                                  | Read table state from URL search parameters              |

## API Reference

### Types

```typescript
interface BifrostConfig {
  endpoint: string;
  headers?: Record<string, string>;
}

interface QueryOptions {
  filter?: TableFilter;
  sort?: SortOption[];
  pagination?: PaginationOptions;
  fields?: string[];
}

interface SortOption {
  field: string;
  direction: 'asc' | 'desc';
}

interface PaginationOptions {
  limit?: number;
  offset?: number;
}

interface TableFilter {
  [field: string]: FieldFilter | string | number | boolean | null;
}

interface FieldFilter {
  _eq?: string | number | boolean | null;
  _neq?: string | number | boolean | null;
  _gt?: string | number;
  _gte?: string | number;
  _lt?: string | number;
  _lte?: string | number;
  _in?: Array<string | number>;
  _nin?: Array<string | number>;
  _contains?: string;
  _ncontains?: string;
  _starts_with?: string;
  _ends_with?: string;
  _null?: boolean;
  _nnull?: boolean;
}

type MutationType = 'insert' | 'update' | 'upsert' | 'delete';
type DiffStrategy = 'shallow' | 'deep';
type ConnectionState = 'connecting' | 'connected' | 'disconnected' | 'error';
type SubscriptionTransport = 'websocket' | 'sse' | 'auto';
type ThemeName = 'modern' | 'classic' | 'minimal' | 'dense';
type AggregateFn = 'sum' | 'avg' | 'min' | 'max' | 'count';
```

## Development

```bash
npm install
npm test
npm run build
npm run lint
npm run format
```

## License

MIT
