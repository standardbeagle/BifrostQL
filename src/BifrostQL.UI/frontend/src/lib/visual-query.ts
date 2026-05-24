/**
 * TypeScript mirror of the C# `VisualQuerySpec` contract
 * (src/BifrostQL.Core/QueryModel/VisualQuery/VisualQuerySpec.cs).
 *
 * This is the wire shape the Access-style query designer sends over the Photino
 * bridge to the server-side SQL builder. Field names are camelCase to match the
 * bridge's System.Text.Json camelCase policy; the "enum" fields are string-literal
 * unions matching the C# string constants exactly, so the spec round-trips
 * field-for-field with no enum converters on either side.
 *
 * Types only — no logic.
 */

export interface VisualQuerySpec {
  tables: VisualTable[];
  columns: VisualColumn[];
  joins: VisualJoin[];
  /** Null/absent = no WHERE clause. */
  filter?: VisualFilter | null;
  /** Null/absent = no row cap (builder still clamps to its own max). */
  rowLimit?: number | null;
}

export interface VisualTable {
  /** Qualified "schema.name". */
  table: string;
  /** Disambiguates the same table added twice (self-join). */
  alias?: string | null;
}

/** Mirrors C# `VisualSort` constants. */
export type VisualSort = 'none' | 'asc' | 'desc';

export interface VisualColumn {
  table: string;
  column: string;
  alias?: string | null;
  /** Whether the column appears in the SELECT list. */
  show: boolean;
  sort: VisualSort;
  /** Ordinal among sorted columns; lower sorts first. Null when not sorted. */
  sortOrder?: number | null;
}

/** Mirrors C# `VisualJoinType` constants. */
export type VisualJoinType = 'inner' | 'left';

export interface VisualJoin {
  leftTable: string;
  /** Parallel to rightColumns — composite FK joins on multiple pairs. */
  leftColumns: string[];
  rightTable: string;
  rightColumns: string[];
  type: VisualJoinType;
}

/** Mirrors C# `VisualFilterOp` constants. */
export type VisualFilterOp = 'and' | 'or' | 'leaf';

/**
 * Filter tree node: a group ('and'/'or' with `children`) or a leaf ('leaf' with
 * `criterion`). The unused arm is null/absent.
 */
export interface VisualFilter {
  op: VisualFilterOp;
  children?: VisualFilter[] | null;
  criterion?: VisualCriterion | null;
}

/** Mirrors C# `VisualFilterOperator` constants — the GraphQL filter operators. */
export type VisualFilterOperator =
  | '_eq'
  | '_neq'
  | '_lt'
  | '_lte'
  | '_gt'
  | '_gte'
  | '_contains'
  | '_in'
  | '_between'
  | '_null';

export interface VisualCriterion {
  table: string;
  column: string;
  operator: VisualFilterOperator;
  /** Scalar for most operators; array for _in/_between; ignored for _null. */
  value?: unknown;
}
