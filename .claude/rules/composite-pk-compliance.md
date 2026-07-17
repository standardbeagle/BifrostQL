# Composite-PK compliance checklist

Applies to every client surface under `examples/edit-db/src` and
`src/BifrostQL.UI/frontend/src`. BifrostQL tables can have composite (multi-column)
and BigInt primary keys; any UI that identifies, navigates, filters, or mutates a
row by its key must hold for both. Reviewers: check every item on a PR that touches
row identity, grid filters, URL/route state, exports, forms, or relationship joins.

## Never index-zero a key or FK column

- Never `primaryKeys[0]`, `sourceColumns[0]`, or `destinationColumns[0]`. Taking the
  first column silently drops the rest of a composite key/FK and mis-targets rows.
- Guarded by `examples/edit-db/src/lib/pk-hygiene.test.ts` (walks the tree, fails on
  `primaryKeys[?.][0]`). Do not add to its allowlist to sneak a hit through.
- Need the first PK column's type? Use `getPkTypes(table)[0]` / `getPkType(table)`,
  not `primaryKeys[0]`.

## Route identity through the shared helpers

- Row identity, key filters, and route encoding go through
  `examples/edit-db/src/lib/row-id.ts` (`rowIdOf`, `pkFilterFor`, `buildPkEqFilter`,
  `encodeRouteParts` / `decodeRouteParts`, `encodePkRoute` / `parsePkRoute`) and
  `examples/edit-db/src/lib/query-builder.ts` (`getPkTypes`, `serializeColumnFilters`
  / `deserializeColumnFilters`). One producer and one consumer per encoding so the
  two sides cannot drift.
- A composite key WHERE is an AND of `{col: {_eq: $var}}` per column (see
  `buildPkEqFilter`) — never a single-column guess.

## BigInt-safe keys must survive URL round-trips

- Carry BigInt/Decimal key and filter values as decimal STRINGS, never JS numbers.
  `9007199254740993 > Number.MAX_SAFE_INTEGER`, so any Number/JSON-number coercion
  rounds the trailing digit and corrupts identity.
- The `cf` (column-filter) URL param round-trip is pinned by
  `query-builder.test.ts` ("round-trips a BigInt PK filter through the cf param
  without precision loss"). Any new URL/route state that carries a key value must
  keep it string-typed and add an equivalent exact-value round-trip test.
- BigInt scalars use the numeric operator set (no `_contains`) and their own GraphQL
  scalar (`BigInt`), not `String` — see `columnFilterOperators` / `getGraphQlType`.

## New relationship-join surfaces: composite or explicit-unsupported

- A relationship join that reads only the first source/destination column assumes a
  single-column FK. If a new surface extends a join, it must either handle composite
  FKs correctly (with a proving test) or surface an explicit "unsupported
  relationship" state — never a silent `column[0]` guess.
- If a surface does not touch relationship joins, it is out of scope for this item;
  say so in review rather than leaving it unaddressed.
