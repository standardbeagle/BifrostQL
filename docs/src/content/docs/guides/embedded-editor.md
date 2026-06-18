---
title: Embeddable Data Editor
description: Drop a complete, schema-driven database editor into any React app with @standardbeagle/edit-db — automatic forms, validated CRUD, foreign-key navigation, and full theming via the --ui-* CSS contract.
---

The same navigator that powers the [BifrostQL desktop app](/BifrostQL/guides/desktop-app/) ships as a standalone React component, `@standardbeagle/edit-db`. Point it at a GraphQL endpoint and you get a fully functional admin UI — data grid, generated forms, validation, and foreign-key navigation — with **zero configuration**. It reads the schema and builds the screens.

## Install

```bash
npm install @standardbeagle/edit-db @tanstack/react-query
```

```tsx
import { Editor } from '@standardbeagle/edit-db';
import '@standardbeagle/edit-db/style.css';

export default function Admin() {
  return (
    <div className="h-screen">
      <Editor uri="/graphql" uiPath="/admin" />
    </div>
  );
}
```

That's the whole integration. The editor introspects the schema at runtime and renders every table.

## What you get

- **Automatic forms** generated from the GraphQL schema — correct control per type, with validation (required, pattern, ranges, custom rules) surfaced straight from BifrostQL's server rules.
- **Data table** with sorting, filtering, and pagination (TanStack Table under the hood).
- **Foreign-key navigation** — relationships are detected automatically; click through from a row to its related records.
- **Many-to-many editing** — attach/detach junction links with optional payload columns.
- **Locale-aware formatting** — dates, relative times, numbers, and percentages via native `Intl`, with the exact value on hover. Control per column with the `display-format` metadata key.
- **Responsive layout** built on Tailwind + shadcn/ui.

## Props

| Prop | Type | Purpose |
|------|------|---------|
| `uri` | `string` | GraphQL endpoint URL |
| `fetcher` | `GraphQLFetcher` | Custom fetcher (auth headers, etc.) — use instead of `uri` |
| `uiPath` | `string` | Base path for the editor's internal routing (e.g. `/admin`) |
| `onLocate` | `(path: string) => void` | Navigation callback to sync with your router |
| `showStats` | `boolean` | Per-table row-count stats (default `false`; off = no extra queries) |

Need auth? Provide a `fetcher` that adds your headers:

```tsx
<Editor
  uiPath="/admin"
  fetcher={async (query, variables) => {
    const res = await fetch('/graphql', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
      body: JSON.stringify({ query, variables }),
    });
    return res.json();
  }}
/>
```

## Theming with the `--ui-*` contract

The editor is themed entirely through CSS custom properties, layered with `@layer` so your overrides win **without specificity battles**. Declare the layer order and set tokens in your app's layer:

```css
/* Your app stylesheet */
@layer editdb-theme, app-theme;

@layer app-theme {
  :root {
    --ui-background: #0b0f14;
    --ui-foreground: #e6edf3;
    --ui-primary: #4f9cf9;
    --ui-primary-foreground: #ffffff;
    --ui-border: #1f2933;
    --ui-accent: #1b2733;
    --ui-destructive: #f85149;
  }
}
```

Because `app-theme` is declared after `editdb-theme`, your values override the editor's defaults cleanly. The full token set:

| Group | Tokens |
|-------|--------|
| Surface | `--ui-background`, `--ui-foreground` |
| Card | `--ui-card`, `--ui-card-foreground` |
| Popover | `--ui-popover`, `--ui-popover-foreground` |
| Primary | `--ui-primary`, `--ui-primary-foreground` |
| Secondary | `--ui-secondary`, `--ui-secondary-foreground` |
| Muted | `--ui-muted`, `--ui-muted-foreground` |
| Accent | `--ui-accent`, `--ui-accent-foreground` |
| Destructive | `--ui-destructive`, `--ui-destructive-foreground` |
| Controls | `--ui-border`, `--ui-input`, `--ui-ring` |

Change a token and every grid, form, and dialog re-themes at once.

## Editor vs. hooks

Use the **Editor** when you want a ready-made admin surface. Reach for [`@bifrostql/react` hooks](/BifrostQL/guides/react-hooks/) when you're building bespoke screens and want query/mutation/table primitives instead of a full UI.
