---
title: Example Projects
description: Runnable example projects in the BifrostQL repository — schema-driven HTML forms, the embeddable React editor, and a minimal host app.
---

The [BifrostQL repository](https://github.com/standardbeagle/BifrostQL) ships runnable examples under `examples/`. Clone the repo and run them directly — they're the fastest way to see a feature working before wiring it into your own project.

```bash
git clone https://github.com/standardbeagle/BifrostQL
cd BifrostQL
```

## `examples/forms-sample` — schema-driven HTML forms

A self-contained ASP.NET Core app demonstrating BifrostQL's server-rendered HTML forms against **in-memory demo data — no database required**:

```bash
dotnet run --project examples/forms-sample/forms-sample.csproj
```

Open `http://localhost:5000`. It demonstrates:

- Insert, update, and delete forms with the correct control per column type (text, number, date, checkbox, file upload, textarea)
- Foreign-key dropdowns (Products → Categories, Orders → Customers/Products) and enum controls
- Metadata-driven inputs — email/tel types, patterns, numeric ranges, file `accept`
- Validation error display with ARIA attributes, plus progressive enhancement (client-side validation, delete confirmation, file preview)
- List views with sortable headers, pagination, and search; detail views with formatted values
- A default stylesheet themed via CSS custom properties

The included `schema.sql` creates the equivalent SQL Server schema if you want to run the same shape against a live BifrostQL connection.

## `examples/edit-db` — the embeddable editor package

The source of [`@standardbeagle/edit-db`](https://www.npmjs.com/package/@standardbeagle/edit-db), the React data editor used throughout the [case studies](/BifrostQL/case-studies/). Browse it to see how the schema-driven forms, data table, and mutation hooks are built; consume it from npm in your own app:

```bash
npm install @standardbeagle/edit-db @tanstack/react-query
```

See the [Embeddable Data Editor guide](/BifrostQL/guides/embedded-editor/) for integration, props, and theming.

## `examples/host-edit-db` — minimal editor host

The smallest possible Vite + React app hosting the editor — a single component pointed at a local BifrostQL endpoint. Use it as a starting template for an admin front end:

```bash
pnpm --dir examples/host-edit-db install
pnpm --dir examples/host-edit-db dev
```

Point the `<Editor uri="..." />` prop at your own BifrostQL endpoint.

## Looking for end-to-end scenarios?

The [case studies](/BifrostQL/case-studies/) walk complete, simulated deployments — a web admin beside a legacy WPF app, a two-tier admin portal, and a multi-tenant SaaS back office — with full server configuration and client code.
