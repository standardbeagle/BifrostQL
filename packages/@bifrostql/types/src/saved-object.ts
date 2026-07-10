/**
 * Saved-object contract — persisted, user-authored objects (queries, forms,
 * reports, dashboards) exposed by the server's `/_saved-objects` endpoint.
 *
 * Mirrors the C# source of truth in `src/BifrostQL.Core/SavedObjects/*.cs`,
 * serialized camelCase with the enum as a camelCase string and nullable fields
 * omitted (`WhenWritingNull`) — so every C# nullable maps to an optional `field?`.
 *
 * Distinct from `SavedViewMetadata` in `./metadata`, which is a read-only grid
 * presentation overlay, not a persisted user object.
 */

/** The kind of user-authored object a {@link SavedObject} holds. */
export type SavedObjectType = 'query' | 'form' | 'report' | 'dashboard';

/**
 * One persisted user-authored object. `definition` is the opaque, type-specific
 * payload the server does not interpret. `version` is an optimistic-concurrency
 * token: a create carries `0` (server persists `1`); an update must carry the
 * version last read, and the server rejects a stale write with HTTP 409.
 */
export interface SavedObject {
  /** Stable client-assigned identifier, unique within its `type`. */
  id: string;
  /** The object kind. */
  type: SavedObjectType;
  /** Human-readable name. */
  name: string;
  /** Optional folder path for organization; omitted = root. */
  folder?: string;
  /** Opaque, type-specific definition payload. */
  definition: unknown;
  /** Optimistic-concurrency version token. */
  version: number;
}
