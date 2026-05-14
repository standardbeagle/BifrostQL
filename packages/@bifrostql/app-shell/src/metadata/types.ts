/**
 * Back-compat re-export. The app-metadata contract types now live in the
 * neutral `@bifrostql/types` package so they can be shared with non-React
 * clients. This module preserves the existing `./metadata/types` import path
 * for `@bifrostql/app-shell` internals and public consumers.
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
