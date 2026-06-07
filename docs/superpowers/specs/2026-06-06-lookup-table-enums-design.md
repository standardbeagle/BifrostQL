# Lookup-table enums → GraphQL — Design

**Date:** 2026-06-06
**Status:** Approved (design); pending implementation plan
**Scope:** Wire the existing-but-unwired lookup-table enum cluster
(`EnumTableConfig`, `EnumTableSchemaGenerator`, `EnumValueSanitizer`,
`EnumValueEntry`) into the BifrostQL GraphQL pipeline so lookup tables marked
with `enum:` metadata produce real GraphQL enum types, and columns that
reference them are typed, filterable, and writable as those enums.

This is the first of two sequenced features. Pivot/cross-tab wiring is a
separate later spec.

## Goal

Given a lookup table annotated in schema metadata:

```
"dbo.status { enum: true }"            # auto value column
"dbo.status { enum: code }"            # explicit value column
"dbo.status { enum: code:display }"    # value + label columns
```

the API exposes `enum statusValues { ACTIVE INACTIVE ... }`, columns that
reference that lookup render as `statusValues`, those columns accept
`FilterTypestatusValuesInput` filters, and reads/filters/writes translate
between the GraphQL enum name and the stored database value.

## Decisions (locked during brainstorming)

| Decision | Choice |
|---|---|
| Column→enum mapping model | **A — value-valued.** Columns hold the value string; map `value ↔ sanitized-name`. No id translation. FK-by-id (Approach B) is out of scope, documented as a follow-up. |
| Column opt-in | **FK detection + metadata override.** A column renders as the enum when its FK targets the enum table's value column, OR it carries `enum-ref: dbo.<table>` metadata. Override wins over detection. |
| Unknown stored value (read) | **Null + structured warning.** The field resolves to null, a warning is logged; the rest of the row is unaffected. |
| Security | **Full.** Enum value loading runs through the same filter-transformer pipeline as a normal read (tenant scoping + soft-delete apply). Enum membership is therefore per-connection/profile. |
| GraphQL surface | Enum type + filter-input type + enum-typed columns + read/filter/write value mapping. |

## Components

### New
- **`EnumValueLoader`** (`src/BifrostQL.Core/Schema/`)
  - Input: `IDbModel` + `IDbConnFactory` (+ the filter-transformer context used
    for normal reads).
  - For each table where `EnumTableConfig.FromTable(table)` is non-null: build a
    `SELECT DISTINCT {valueColumn}[, {labelColumn}] FROM {table}` through the
    **filter-transformer pipeline** (so tenant-filter / soft-delete WHERE
    clauses are injected), execute it, pass raw values to
    `EnumValueSanitizer.SanitizeAll`, and collect the resulting
    `IReadOnlyList<EnumValueEntry>`.
  - Output: `IReadOnlyDictionary<string, IReadOnlyList<EnumValueEntry>>` keyed by
    table DbName. A table whose distinct values are empty or all unsanitizable
    is omitted (its columns stay scalar).
  - Resilience: a DB failure loading one enum table degrades that table to
    scalar + a warning; it does not fail the whole schema build.

- **`EnumColumnMap`** (`src/BifrostQL.Core/Schema/`)
  - Built from the model + the loaded enum-values map.
  - `TryGetEnum(IColumnDto column) → (string enumName, IReadOnlyList<EnumValueEntry> entries)?`
  - Resolution order: explicit `enum-ref: dbo.<table>` metadata on the column →
    else FK whose target is an enum table's value column → else none.
  - Also exposes reverse lookups used by transformers:
    `TryValueToName(column, dbValue) → name?` and
    `TryNameToValue(column, name) → dbValue?`.

### Reused unchanged
- `EnumTableConfig` (metadata parsing, `GraphQlEnumName = {table}Values`)
- `EnumTableSchemaGenerator` (`enum {Table}Values {...}`,
  `FilterType{Table}ValuesInput`)
- `EnumValueSanitizer` / `EnumValueEntry(GraphQlName, DatabaseValue)`

## Wiring points

1. **`DbModelLoader`** — add `LoadEnumValuesAsync(IDbModel model)` returning the
   enum-values map (uses its `IDbConnFactory`). Runs once per connection.
2. **`ProfileModelCache`** — cache the enum-values map next to the shared
   `SchemaData` read (same lifetime; cleared by `Reset`). All per-profile schema
   builds reuse it.
3. **`SchemaGenerator.SchemaTextFromModel`** — accept the enum map. For each
   enum table append the enum type + `FilterType{Table}ValuesInput`.
   `TableSchemaGenerator` consults an `EnumColumnMap` so enum columns render
   their field type as `{Table}Values` and their filter argument as
   `FilterType{Table}ValuesInput` instead of the scalar/default.

## Data flow

- **Read projection.** When projecting an enum column's value:
  `EnumColumnMap.TryValueToName(column, storedValue)`.
  - hit → emit `name`
  - miss (value absent from the build-time snapshot, or null) → **null + a
    structured warning** naming table/column/value; the rest of the row resolves.
- **Filter.** A filter transformer rewrites enum-named filter operands
  (`_eq/_neq/_in/_nin`) to their `DatabaseValue` before SQL parameter binding.
  An unknown name (client-supplied) → validation error (fail fast).
- **Write** (insert/update/upsert). A mutation transformer rewrites enum-named
  input values for enum columns to `DatabaseValue` before the mutation SQL. An
  unknown name → mutation error.

Both transformers slot into the existing transformer pipeline
(`IFilterTransformer` / `IMutationTransformer`) at the data-filtering priority
band; they consult `EnumColumnMap` from the request's resolved model.

## Security

`EnumValueLoader` issues its distinct-value queries through the same
filter-transformer pipeline as a normal table read, so tenant-filter and
soft-delete WHERE clauses apply. Consequence (intended): enum membership is
scoped per connection/profile — a tenant only sees its own enum members. This
aligns with the existing per-connection `ProfileModelCache` lifetime; no new
caching model is introduced.

## Error handling

| Situation | Behavior |
|---|---|
| One enum table fails to load at build time | That table degrades to scalar columns + warning; schema build succeeds. |
| Read: stored value not in snapshot / null | Field → null + structured warning. |
| Filter: client sends unknown enum name | Validation error. |
| Write: input has unknown enum name for an enum column | Mutation error. |
| Lookup table empty or all values unsanitizable | No enum emitted; columns stay scalar. |

## Testing

- **Unit**
  - `EnumColumnMap` resolution: metadata override vs FK-targets-value vs neither;
    reverse `value→name` / `name→value`.
  - `EnumValueLoader`: sanitization, empty set, all-unsanitizable, per-table load
    failure degradation.
- **Schema (snapshot)**
  - Emitted `enum {Table}Values`, `FilterType{Table}ValuesInput`, and an
    enum-typed column field + filter arg.
- **Integration (SqlServer / Postgres / MySQL / SQLite)**
  - Seed a lookup table + a referencing table; assert: read maps value→name,
    filter-by-enum returns the right rows, insert/update by enum name persists
    the underlying value, and a value absent from the snapshot resolves to
    null + warning.
  - Security: with a tenant-filter active, enum membership reflects only the
    tenant's rows.

## Out of scope (follow-ups)

- **Approach B (FK-by-id enums):** columns storing the lookup PK rather than the
  value string; requires `id ↔ value ↔ name` translation and an id on
  `EnumValueEntry`.
- **Pivot/cross-tab wiring:** separate spec.
- **Live enum refresh:** enum membership is fixed at schema-build time per
  connection; refreshing requires a reconnect / `ProfileModelCache.Reset`.
