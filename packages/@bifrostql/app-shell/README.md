# @bifrostql/app-shell

> **Status: Experimental.** This package is not consumed by the shipped BifrostQL product. The Desktop UI (`src/BifrostQL.UI/frontend`) ships with `@standardbeagle/edit-db`, which has its own fetcher, query-builder, and mutation hooks. `@bifrostql/app-shell` (built on `@bifrostql/react`) is a parallel, in-progress API surface with zero current importers — expect breaking changes without notice. See `AGENTS.md` for the architecture note on this split.

Reusable React app shell and CRUD primitives for [BifrostQL](https://github.com/standardbeagle/bifrostql) — navigation, auth, metadata-driven fields, routing, and screens on top of `@bifrostql/react`.

## Installation

```bash
npm install @bifrostql/app-shell @bifrostql/react @tanstack/react-query react react-dom
```

### Peer Dependencies

| Package                 | Version   |
| ----------------------- | --------- |
| `@bifrostql/react`      | ^0.1.0    |
| `react`                 | >= 18.0.0 |
| `react-dom`             | >= 18.0.0 |
| `@tanstack/react-query` | >= 5.0.0  |

## Contents

See `src/` for the current surface: `app-shell-provider`, `auth`, `fields`, `metadata`, `nav`, `routing`, and `screens`.
