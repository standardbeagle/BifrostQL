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
  '_ends_with',
  '_between',
  '_null',
  '_nnull',
]);

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
