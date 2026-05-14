import type { AppMetadata } from '@bifrostql/app-shell';
import type { TableFilter } from '@bifrostql/react';

/**
 * Declarative email-segment definition as it appears in the app-metadata
 * overlay's top-level `emailSegments` map.
 *
 * A segment is a *named, filter-based audience definition* — entity key plus a
 * filter — and nothing more. It is intentionally definition-only: the overlay
 * carries no sending configuration (no SMTP, queue, or send action), so an
 * `emailSegments` entry can never describe how mail is delivered, only *who*
 * the audience is.
 *
 * The filter shape is the same opaque, client-interpreted `field = value`
 * expression list used by `SavedViewMetadata.filters`, so segment authoring
 * mirrors saved-view authoring exactly.
 */
export interface EmailSegmentMetadata {
  /** Human-readable name of the segment. */
  name?: string;
  /**
   * Qualified entity key the segment's audience is drawn from
   * (e.g. `main.members`).
   */
  entity: string;
  /** Opaque, client-interpreted filter expressions defining the audience. */
  filters?: string[];
}

/**
 * The top-level `emailSegments` overlay shape: an app-metadata document that
 * additionally carries declarative email-segment definitions keyed by a stable
 * identifier. `emailSegments` is an additive overlay key — the C# host's
 * `System.Text.Json` reader ignores unknown members, so it is consumed only by
 * app-builder tooling and this SPA, never by the query host.
 */
export interface AppMetadataWithSegments extends AppMetadata {
  /** Named email-segment definitions keyed by a stable identifier. */
  emailSegments?: Record<string, EmailSegmentMetadata>;
}

/**
 * An email segment from the overlay, paired with its query-ready table filter.
 */
export interface EmailSegmentOption {
  /** Stable identifier — the overlay's `emailSegments` key. */
  id: string;
  /** Human-readable name; falls back to the id when the overlay omits `name`. */
  name: string;
  /** Qualified entity key the audience is drawn from. */
  entityKey: string;
  /**
   * `BifrostTable` filter built from the segment's `filters` expressions. Only
   * the simple `field = value` equality form is understood — other expressions
   * are ignored — so a segment with no parseable clauses yields an empty
   * filter. This is the same parser the saved-view picker uses.
   */
  filter: TableFilter;
}

/**
 * Translate one segment's opaque `filters` expressions into a
 * {@link TableFilter}. Only the `field = value` equality form is recognised —
 * the same shape and parser behaviour as the saved-view picker — so any other
 * expression is ignored.
 */
function buildSegmentFilter(segment: EmailSegmentMetadata): TableFilter {
  const filter: TableFilter = {};
  for (const expr of segment.filters ?? []) {
    const match = expr.match(/^\s*(\w+)\s*=\s*(.+?)\s*$/);
    if (!match) {
      continue;
    }
    const field = match[1];
    const value = match[2].replace(/^['"]|['"]$/g, '');
    filter[field] = { _eq: value };
  }
  return filter;
}

/**
 * Derive the email-segment options from the app-metadata overlay's top-level
 * `emailSegments` map.
 *
 * Every entry becomes one option, in declaration order, so the segment list —
 * and therefore the available audiences — comes entirely from the overlay,
 * never from hardcoded segment names. A segment whose `entity` is blank is
 * dropped, since it has no audience source.
 */
export function getEmailSegmentOptions(
  metadata: AppMetadataWithSegments | undefined,
): EmailSegmentOption[] {
  const segments = metadata?.emailSegments;
  if (!segments) {
    return [];
  }
  return Object.entries(segments)
    .filter(([, segment]) => Boolean(segment.entity))
    .map(([id, segment]) => ({
      id,
      name: segment.name ?? id,
      entityKey: segment.entity,
      filter: buildSegmentFilter(segment),
    }));
}
