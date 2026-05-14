# BifrostQL Agent Guide

This repo has several generated surfaces and string-driven extension points. Treat this file as the maintainer map before making automated edits.

## Edit Source, Not Generated Output

- Frontend source for the desktop UI lives in `src/BifrostQL.UI/frontend`.
- `src/BifrostQL.UI/wwwroot` is Vite output from that frontend. Do not hand-edit bundled JS, CSS, font files, or `index.html`; rebuild them with `pnpm --dir src/BifrostQL.UI/frontend build`.
- `src/**/bin`, `src/**/obj`, `node_modules`, package `dist`, coverage, and Storybook output are build artifacts.

## Package Manager

- Use pnpm 11.1.1 from the root `packageManager` field.
- Use the root `pnpm-lock.yaml` for all workspace packages, including docs.
- Do not add `package-lock.json` or nested pnpm lockfiles unless a package is intentionally removed from `pnpm-workspace.yaml`.
- Prefer `pnpm --dir <package> <script>` or `pnpm --filter <package> <script>` over `npm`, `npx`, or directory-changing script chains.

## Metadata Keys

- Metadata key names belong in `src/BifrostQL.Core/Model/MetadataKeys.cs`.
- Core implementation code should use those constants for metadata dictionary lookups and module names.
- When adding metadata, update `MetadataKeys`, metadata validation allow-lists, docs, and tests together.
- Keep tenant isolation and soft-delete keys especially consistent; they affect security and mutation semantics.

## GraphQL Query Builders

- Do not interpolate user-provided table, field, operator, or type names directly into GraphQL text.
- Use the existing query-builder validation helpers and schema-derived names.
- The edit-db app supports composite primary keys. Use the helpers in `examples/edit-db/src/lib/row-id.ts` and `examples/edit-db/src/lib/query-builder.ts`; avoid direct `primaryKeys[0]` shortcuts.
- Relationship joins that use first source/destination columns are single-column FK assumptions, not composite-PK helpers. Document and test any expansion beyond that.

## React Table Hook

- `packages/@bifrostql/react/src/hooks/use-bifrost-table.ts` is intentionally broad for public API compatibility. Prefer extracting internals into focused helpers/hooks over adding more cross-cutting state directly to the main hook.
- Check URL sync, local storage, editing, export, grouping, pagination, and virtualization behavior when changing this hook.

## Transport

- The BifrostQL.UI header can probe HTTP and binary transports, but `@standardbeagle/edit-db` still executes editor queries through its HTTP `uri` prop.
- Do not treat the transport toggle as full editor transport routing until the editor accepts a `QueryTransport` or equivalent hook.

## Docs Authority

- Canonical user docs live under `docs/src/content/docs`.
- `docs-research` is exploratory/reference material and may be stale. Do not copy behavior from it without checking source and canonical docs.
