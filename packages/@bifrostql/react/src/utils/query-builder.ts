import type {
  QueryOptions,
  SortOption,
  AdvancedFilter,
  CompoundFilter,
} from '../types';
import { assertFilterOperator, assertGraphqlName } from './graphql-identifiers';

function isCompoundFilter(filter: AdvancedFilter): filter is CompoundFilter {
  return '_and' in filter || '_or' in filter;
}

function buildFilterObject(filter: AdvancedFilter): string {
  if (isCompoundFilter(filter)) {
    const parts: string[] = [];
    if (filter._and) {
      const inner = filter._and.map((f) => `{ ${buildFilterObject(f)} }`);
      parts.push(`and: [${inner.join(', ')}]`);
    }
    if (filter._or) {
      const inner = filter._or.map((f) => `{ ${buildFilterObject(f)} }`);
      parts.push(`or: [${inner.join(', ')}]`);
    }
    return parts.join(', ');
  }

  const entries = Object.entries(filter);
  if (entries.length === 0) return '';

  const parts = entries.map(([field, value]) => {
    assertGraphqlName(field, 'filter field');
    if (value === null) return `${field}: { _null: true }`;
    if (typeof value !== 'object')
      return `${field}: { _eq: ${JSON.stringify(value)} }`;

    const ops = Object.entries(value as Record<string, unknown>)
      .map(([op, val]) => {
        assertFilterOperator(op);
        if (op === '_between' && Array.isArray(val) && val.length === 2) {
          return `_gte: ${JSON.stringify(val[0])}, _lte: ${JSON.stringify(val[1])}`;
        }
        // The schema exposes only `_null: Boolean`; map the client-side
        // `_nnull` convenience onto it so it round-trips to a real query
        // instead of emitting an operator the server rejects.
        if (op === '_nnull') {
          return `_null: ${val ? 'false' : 'true'}`;
        }
        return `${op}: ${JSON.stringify(val)}`;
      })
      .join(', ');
    return `${field}: { ${ops} }`;
  });

  return parts.join(', ');
}

function buildFilterArgs(filter: AdvancedFilter): string {
  const content = buildFilterObject(filter);
  if (!content) return '';
  return `filter: { ${content} }`;
}

function buildSortArgs(sort: SortOption[]): string {
  if (sort.length === 0) return '';
  const parts = sort.map((s) => {
    assertGraphqlName(s.field, 'sort field');
    if (s.direction !== 'asc' && s.direction !== 'desc') {
      throw new Error(`Invalid GraphQL sort direction: ${String(s.direction)}`);
    }
    return `${s.field}_${s.direction}`;
  });
  return `sort: [${parts.join(', ')}]`;
}

function assertPaginationValue(value: number, kind: string): void {
  if (!Number.isSafeInteger(value) || value < 0) {
    throw new Error(`Invalid GraphQL pagination ${kind}: ${String(value)}`);
  }
}

function buildPaginationArgs(limit?: number, offset?: number): string {
  const parts: string[] = [];
  if (limit !== undefined) {
    assertPaginationValue(limit, 'limit');
    parts.push(`limit: ${limit}`);
  }
  if (offset !== undefined) {
    assertPaginationValue(offset, 'offset');
    parts.push(`offset: ${offset}`);
  }
  return parts.join(', ');
}

/**
 * Build a GraphQL query string for a BifrostQL table.
 *
 * Combines filter, sort, pagination, and field-selection options into a
 * syntactically valid GraphQL query string. The result can be passed directly
 * to {@link executeGraphQL} or {@link useBifrost}.
 *
 * @param table - The database table name to query.
 * @param options - Filter, sort, pagination, and field-selection options.
 * @returns A GraphQL query string.
 *
 * @example
 * ```ts
 * const query = buildGraphqlQuery('users', {
 *   filter: { active: true },
 *   sort: [{ field: 'name', direction: 'asc' }],
 *   pagination: { limit: 25 },
 *   fields: ['id', 'name', 'email'],
 * });
 * ```
 */
export function buildGraphqlQuery(
  table: string,
  options: QueryOptions = {},
): string {
  const { filter, sort, pagination, fields } = options;
  assertGraphqlName(table, 'table');

  const args: string[] = [];
  if (filter) args.push(buildFilterArgs(filter));
  if (sort && sort.length > 0) args.push(buildSortArgs(sort));
  if (pagination) {
    const pag = buildPaginationArgs(pagination.limit, pagination.offset);
    if (pag) args.push(pag);
  }

  const argStr = args.length > 0 ? `(${args.join(', ')})` : '';
  const fieldStr =
    fields && fields.length > 0
      ? fields
          .map((field) => {
            assertGraphqlName(field, 'selection field');
            return field;
          })
          .join('\n    ')
      : '__typename';

  return `{
  ${table}${argStr} {
    ${fieldStr}
  }
}`;
}
