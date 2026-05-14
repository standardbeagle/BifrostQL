/**
 * TypeScript mirror of the BifrostQL app-metadata overlay JSON contract.
 *
 * The C# source of truth is `src/BifrostQL.Core/AppMetadata/*.cs`. The overlay
 * is serialized by `AppMetadataJson` with:
 * - `PropertyNamingPolicy = CamelCase` (property names are camelCase)
 * - `JsonStringEnumConverter(CamelCase)` (enums serialize as camelCase strings)
 * - `DefaultIgnoreCondition = WhenWritingNull` (null optionals are omitted)
 * - `DictionaryKeyPolicy = null` (dictionary keys are emitted verbatim)
 *
 * Because nulls are omitted on the wire, every C# nullable property is modeled
 * here as an optional field rather than `T | null`.
 */

/**
 * The presentation kind for a {@link RelationshipMetadata} entry. Mirrors the
 * C# `RelationshipKind` enum, serialized as a camelCase string.
 */
export type RelationshipKind =
  | 'foreignKeySelector'
  | 'childCollection'
  | 'nestedPanel';

/**
 * Field-level presentation and behavior metadata. Mirrors C# `FieldMetadata`.
 */
export interface FieldMetadata {
  /** Widget hint for rendering this field (e.g. `text`, `select`). */
  widget?: string;
  /** Opaque, client-interpreted validation expression or rule name. */
  validation?: string;
  /** Whether the field is visible in the UI. C# default: `true`. */
  visible?: boolean;
  /** Whether the field is read-only in the UI. C# default: `false`. */
  readOnly?: boolean;
  /** Help text shown alongside the field. */
  helpText?: string;
  /** Display group this field belongs to. */
  group?: string;
}

/**
 * A named, reusable view definition within a grid preset. Mirrors C#
 * `SavedViewMetadata`.
 */
export interface SavedViewMetadata {
  /** Human-readable name of the saved view. */
  name?: string;
  /** Ordered field names shown as columns in this view. */
  columns?: string[];
  /** Opaque, client-interpreted filter expressions applied by this view. */
  filters?: string[];
  /** Ordered sort directives for this view (e.g. `created_at desc`). */
  sort?: string[];
}

/**
 * Grid-preset presentation metadata for an entity's list/table view. Mirrors
 * C# `GridPresetMetadata`. The C# property is named `Grid` on `EntityMetadata`,
 * so this is the entity's `grid` field on the wire.
 */
export interface GridMetadata {
  /** Ordered field names shown as columns by default. */
  defaultColumns?: string[];
  /** Opaque, client-interpreted default filter expressions. */
  defaultFilters?: string[];
  /** Ordered default sort directives (e.g. `created_at desc`). */
  defaultSort?: string[];
  /** Named saved views keyed by a stable identifier. */
  savedViews?: Record<string, SavedViewMetadata>;
  /** Opaque, client-interpreted bulk-action identifiers. */
  bulkActions?: string[];
}

/**
 * Relationship presentation metadata for an entity. Mirrors C#
 * `RelationshipMetadata`.
 */
export interface RelationshipMetadata {
  /** Qualified table name of the related entity (e.g. `sales.orders`). */
  targetEntity?: string;
  /** Relationship presentation kind. C# default: `foreignKeySelector`. */
  kind?: RelationshipKind;
  /** Field name carrying the foreign key. */
  foreignKeyField?: string;
  /** Ordered target-entity field names shown for child collections/panels. */
  displayColumns?: string[];
  /** Human-readable label for the relationship as shown on this entity. */
  label?: string;
}

/**
 * Entity-level presentation metadata. Mirrors C# `EntityMetadata`.
 */
export interface EntityMetadata {
  /** Human-readable label for the entity. */
  label?: string;
  /** Icon hint for the entity (icon name or token). */
  icon?: string;
  /** Field name(s) used to render a short display representation of a row. */
  displayFields?: string[];
  /** Navigation placement hint (section or menu group). */
  navPlacement?: string;
  /** Field-level metadata keyed by field name. */
  fields?: Record<string, FieldMetadata>;
  /** Grid-preset metadata for the entity's list/table view. */
  grid?: GridMetadata;
  /** Relationship metadata keyed by relationship name. */
  relationships?: Record<string, RelationshipMetadata>;
}

/**
 * Root aggregate of the app-metadata overlay. Mirrors the C# `AppMetadataModel`
 * record; named `AppMetadata` here because TypeScript has no namespace
 * collision constraint.
 */
export interface AppMetadata {
  /** Entity-level metadata keyed by qualified table name (e.g. `dbo.users`). */
  entities?: Record<string, EntityMetadata>;
}
