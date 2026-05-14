# @bifrostql/types

Shared TypeScript contract types for BifrostQL clients (SPA, React Native, and
future consumers). Types only — no runtime behavior.

## Exports

| Subpath                     | Contents                                                        |
| --------------------------- | --------------------------------------------------------------- |
| `@bifrostql/types`          | Hand-authored contract types (metadata, query) + the generated namespace |
| `@bifrostql/types/generated`| Only the `@bifrostql/codegen`-emitted proto-derived domain types |

## Generated domain types

The files under `src/generated/` are emitted by `@bifrostql/codegen` from a
BifrostQL `.proto` schema — **do not hand-edit them**. The `src/index.ts` barrel
re-exports the generated namespace, so SPA code imports generated types from
`@bifrostql/types` (or `@bifrostql/types/generated`) rather than an app-local
`./generated` directory.

### Regeneration

Build the codegen CLI, then point it at a `.proto` schema with the shared
package's `src/generated` dir as the output:

```bash
pnpm --filter @bifrostql/codegen build
node packages/bifrost-codegen/dist/cli.js \
  --proto-file packages/bifrost-codegen/fixtures/sample.proto \
  --out packages/@bifrostql/types/src/generated
pnpm --filter @bifrostql/types build
```

`fixtures/sample.proto` is a checked-in sample schema. Against a running server,
swap `--proto-file` for `--endpoint <ws-url>` once the server exposes its proto
text (see the codegen CLI `--help`).

## Scripts

```bash
pnpm build       # tsc → dist/
pnpm test        # vitest (type-level contract assertions)
pnpm lint        # eslint
```
