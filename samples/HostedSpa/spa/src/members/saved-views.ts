import type { EntityMetadata, SavedViewMetadata } from '@bifrostql/app-shell';
import type { TableFilter } from '@bifrostql/react';

/** A saved view from the overlay, paired with its picker-ready table filter. */
export interface SavedViewOption {
  /** Stable identifier — the overlay's `savedViews` key. */
  id: string;
  /** Human-readable name; falls back to the id when the overlay omits `name`. */
  name: string;
  /**
   * `BifrostTable` filter built from the view's `filters` expressions. Only the
   * simple `field = value` equality form is understood — other expressions are
   * ignored — so a view with no parseable clauses yields an empty filter.
   */
  filter: TableFilter;
}

/**
 * Translate one saved view's opaque `filters` expressions into a
 * {@link TableFilter}. The overlay's filter expressions are client-interpreted
 * strings; only the `field = value` equality form is recognised, which is the
 * shape the operational member views use.
 */
function buildViewFilter(view: SavedViewMetadata): TableFilter {
  const filter: TableFilter = {};
  for (const expr of view.filters ?? []) {
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
 * Derive the saved-view picker options for an entity from its grid preset.
 *
 * Every entry under `grid.savedViews` becomes one option, in declaration order,
 * so the picker — and therefore the available views — comes entirely from the
 * overlay, never from hardcoded view names.
 */
export function getSavedViewOptions(
  entity: EntityMetadata | undefined,
): SavedViewOption[] {
  const savedViews = entity?.grid?.savedViews;
  if (!savedViews) {
    return [];
  }
  return Object.entries(savedViews).map(([id, view]) => ({
    id,
    name: view.name ?? id,
    filter: buildViewFilter(view),
  }));
}
