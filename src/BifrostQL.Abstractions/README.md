# BifrostQL.Abstractions

Stable, dependency-light contracts for authoring **BifrostQL** modules and SQL
dialects. This package was carved out of `BifrostQL.Core` so that third-party
module and dialect authors can compile against a surface that changes under an
explicit versioning policy, instead of tracking pre-1.0 `Core`.

## What lives here

- **Metadata vocabulary** — `MetadataKeys` (the canonical metadata key names a
  module reads/writes).
- **Error contract** — `BifrostExecutionError` (the exception modules throw and
  transports map onto their wire).
- **Resolver / type-mapping seams** — `IBifrostFieldContext`, `ITypeMapper`.
- **SQL-generation primitives** — `ParameterizedSql`, `SqlParameterCollection` /
  `SqlParameterInfo`, `SqlParameterNames`, `SqlColumnDefinition` / `SqlColumnKind`,
  and the full-text request contract `FtsPredicateRequest` / `FtsTerm` /
  `FtsQueryParser`.

Every type keeps its original `BifrostQL.Core.*` namespace and is
`[TypeForwardedTo]` from `BifrostQL.Core`, so existing consumers recompile and
bind unchanged.

## Dependencies

**None** beyond the base class library. This package references no ASP.NET Core,
no GraphQL, and no database driver package — that is what lets it version
independently.

## Versioning

`BifrostQL.Abstractions` commits to **Semantic Versioning** from `1.0.0`:

- **MAJOR** — a breaking change to any contract in this package (removed/renamed
  member, changed signature or semantics).
- **MINOR** — a backward-compatible addition (new member with a default, new
  type).
- **PATCH** — a backward-compatible fix with no surface change.

`BifrostQL.Core` remains **pre-1.0** and does **not** carry this guarantee; the
stable surface is exactly what has been promoted into this package.

## Scope note

`ISqlDialect` / `SqlDialectBase`, `TableFilter` / `TableFilterBuilder`, the
`IFilterTransformer` / `IMutationTransformer` / `IQueryObserver` module
transformer contracts, and `IDbConnFactory` are **not yet** in this package.
Extracting them cleanly requires first relocating the `Core` Model DTO graph
(`ColumnDto` and the `IDbTable`/`IDbModel` cluster), which has a `Core`-internal
dependency (`DbModel.Pluralizer`) and is entangled with the module/FTS
internals. That internal/parity-boundary rework is tracked separately; these
contracts move here once it lands.
