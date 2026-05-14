/**
 * `@bifrostql/types` — shared TypeScript contract types for BifrostQL clients.
 *
 * Consumed by `@bifrostql/react`, `@bifrostql/app-shell`, and future clients
 * (e.g. React Native). Types only — no runtime behavior.
 */

export type {
  RelationshipKind,
  FieldMetadata,
  SavedViewMetadata,
  GridMetadata,
  RelationshipMetadata,
  EntityMetadata,
  AppMetadata,
} from './metadata';

export type {
  TableFilter,
  FieldFilter,
  CompoundFilter,
  AdvancedFilter,
  PaginationOptions,
  SortOption,
  QueryOptions,
} from './query';

/**
 * Proto-derived domain types emitted by `@bifrostql/codegen` into
 * `./generated`. Regenerate with the command documented in README.md. Also
 * available as the `@bifrostql/types/generated` subpath export for consumers
 * that want only the generated namespace.
 */
export * from './generated';
