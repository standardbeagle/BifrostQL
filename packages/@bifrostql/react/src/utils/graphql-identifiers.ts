const GRAPHQL_NAME = /^[_A-Za-z][_0-9A-Za-z]*$/;
const FILTER_OPERATORS = new Set([
  '_eq',
  '_neq',
  '_gt',
  '_gte',
  '_lt',
  '_lte',
  '_in',
  '_nin',
  '_contains',
  '_ncontains',
  '_starts_with',
  '_nstarts_with',
  '_ends_with',
  '_nends_with',
  '_like',
  '_nlike',
  '_between',
  '_nbetween',
  '_null',
  // Client-only convenience: translated to `_null: false` by the query
  // builder before it reaches GraphQL text (the schema has no `_nnull`).
  '_nnull',
]);

/** Non-throwing check: is `value` a syntactically valid GraphQL name? */
export function isGraphqlName(value: string): boolean {
  return GRAPHQL_NAME.test(value);
}

/** Non-throwing check: is `value` a filter operator the schema accepts? */
export function isFilterOperator(value: string): boolean {
  return FILTER_OPERATORS.has(value);
}

export function assertGraphqlName(value: string, kind: string): void {
  if (!GRAPHQL_NAME.test(value)) {
    throw new Error(`Invalid GraphQL ${kind}: ${value}`);
  }
}

export function assertFilterOperator(value: string): void {
  if (!FILTER_OPERATORS.has(value)) {
    throw new Error(`Invalid GraphQL filter operator: ${value}`);
  }
}
