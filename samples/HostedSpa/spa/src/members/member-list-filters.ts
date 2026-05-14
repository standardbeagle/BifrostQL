import type { EntityMetadata, FieldMetadata } from '@bifrostql/app-shell';
import type { TableFilter } from '@bifrostql/react';

/** A metadata-driven filter control descriptor for the member list screen. */
export interface FilterControl {
  /** Field name the control filters on. */
  field: string;
  /** Human-readable label (the field name; the overlay carries no field labels). */
  label: string;
  /**
   * `select` when the metadata widget is `select` *and* candidate values were
   * discovered in the overlay's saved views / default filters; otherwise
   * `text` (a free-text contains search).
   */
  kind: 'select' | 'text';
  /** Discovered option values for `select` controls. */
  options: string[];
}

/**
 * Parse `field = value` clauses out of an app-metadata filter expression list
 * (e.g. `grid.defaultFilters` or a saved view's `filters`). The overlay's
 * filter expressions are opaque strings; only the simple equality form is
 * understood here, which is enough to discover candidate values for a
 * select-style filter control.
 */
function discoverFilterValues(
  field: string,
  expressions: string[] | undefined,
): string[] {
  if (!expressions) {
    return [];
  }
  const values: string[] = [];
  for (const expr of expressions) {
    const match = expr.match(/^\s*(\w+)\s*=\s*(.+?)\s*$/);
    if (match && match[1] === field) {
      const value = match[2].replace(/^['"]|['"]$/g, '');
      if (!values.includes(value)) {
        values.push(value);
      }
    }
  }
  return values;
}

/**
 * Derive the list view's filter controls from entity metadata.
 *
 * One control is produced per visible field in the grid's `defaultColumns`
 * whose widget is `text` or `select`. `select` widgets become dropdowns when
 * the overlay's saved views or default filters reveal candidate values;
 * otherwise they fall back to a free-text contains search. The set of controls
 * — and therefore the search/status/tag filters — comes entirely from the
 * overlay, never from hardcoded field names.
 */
export function buildFilterControls(entity: EntityMetadata): FilterControl[] {
  const fields: Record<string, FieldMetadata> = entity.fields ?? {};
  const columnOrder =
    entity.grid?.defaultColumns && entity.grid.defaultColumns.length > 0
      ? entity.grid.defaultColumns
      : Object.keys(fields);

  const savedViewFilters = Object.values(entity.grid?.savedViews ?? {}).flatMap(
    (view) => view.filters ?? [],
  );
  const allFilterExpressions = [
    ...(entity.grid?.defaultFilters ?? []),
    ...savedViewFilters,
  ];

  return columnOrder
    .map((field): FilterControl | null => {
      const meta = fields[field];
      if (!meta || meta.visible === false) {
        return null;
      }
      const widget = meta.widget;
      if (widget !== 'text' && widget !== 'select') {
        return null;
      }
      const options =
        widget === 'select'
          ? discoverFilterValues(field, allFilterExpressions)
          : [];
      return {
        field,
        label: field,
        kind: widget === 'select' && options.length > 0 ? 'select' : 'text',
        options,
      };
    })
    .filter((control): control is FilterControl => control !== null);
}

/**
 * Build the `BifrostTable` filter object from the active filter-control values.
 *
 * Text controls contribute a case-insensitive `_contains` clause; select
 * controls contribute an `_eq` clause. Empty values are omitted so they do not
 * constrain the query.
 */
export function buildTableFilter(
  controls: FilterControl[],
  values: Record<string, string>,
): TableFilter {
  const filter: TableFilter = {};
  for (const control of controls) {
    const value = values[control.field]?.trim();
    if (!value) {
      continue;
    }
    if (control.kind === 'select') {
      filter[control.field] = { _eq: value };
    } else {
      filter[control.field] = { _contains: value };
    }
  }
  return filter;
}
