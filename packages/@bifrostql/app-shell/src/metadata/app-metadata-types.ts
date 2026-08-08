/**
 * Re-export of the app-metadata contract types, which live in the neutral
 * `@bifrostql/types` package so they can be shared with non-React clients.
 * This module gives `@bifrostql/app-shell` internals and public consumers a
 * single local import path for them.
 */
export type {
  RelationshipKind,
  FieldMetadata,
  SavedViewMetadata,
  GridMetadata,
  RelationshipMetadata,
  EntityMetadata,
  AppMetadata,
} from '@bifrostql/types';
