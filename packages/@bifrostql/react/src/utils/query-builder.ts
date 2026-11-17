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

/**
 * Translate a client filter into the server's `TableFilter<Name>Input` value.
 * The result travels as the `$filter` variable, never as GraphQL text, so a
 * filter value cannot change the document and every distinct value reuses
 * one parsed document. Field names and operators are still validated: a bad
 * name is a programming error worth failing on here, not a server round-trip.
 */
export function buildFilterInput(
  filter: AdvancedFilter,
): Record<string, unknown> {
  if (isCompoundFilter(filter)) {
    const input: Record<string, unknown> = {};
    if (filter._and) input.and = filter._and.map(buildFilterInput);
    if (filter._or) input.or = filter._or.map(buildFilterInput);
    return input;
  }

  const input: Record<string, unknown> = {};
  for (const [field, value] of Object.entries(filter)) {
    assertGraphqlName(field, 'filter field');
    if (value === null) {
      input[field] = { _null: true };
      continue;
    }
    if (typeof value !== 'object') {
      input[field] = { _eq: value };
      continue;
    }
    const ops: Record<string, unknown> = {};
    for (const [op, val] of Object.entries(value as Record<string, unknown>)) {
      assertFilterOperator(op);
      if (op === '_between' && Array.isArray(val) && val.length === 2) {
        ops._gte = val[0];
        ops._lte = val[1];
      } else if (op === '_nnull') {
        // The schema exposes only `_null: Boolean`; map the client-side
        // `_nnull` convenience onto it instead of sending an operator the
        // server rejects.
        ops._null = !val;
      } else {
        ops[op] = val;
      }
    }
    input[field] = ops;
  }
  return input;
}

/** The server's filter input type for a table: `TableFilter<GraphQlName>Input`. */
export function tableFilterTypeName(table: string): string {
  assertGraphqlName(table, 'table');
  return `TableFilter${table}Input`;
}

function buildSortArgs(sort: readonly SortOption[]): string {
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

/** A GraphQL document plus the variables it declares. */
export interface GraphqlRequest {
  query: string;
  variables: Record<string, unknown>;
}

/**
 * Build the GraphQL request for a BifrostQL table: document text plus
 * variables.
 *
 * The filter travels as a `$filter: TableFilter<table>Input` variable (see
 * {@link buildFilterInput}); only validated names, sort enums and integer
 * pagination appear in the text. Pass both halves to {@link executeGraphQL} or
 * {@link useBifrost}.
 *
 * BifrostQL wraps every top-level table query in a paged envelope — the
 * table field's type carries `data`/`total`/`offset`/`limit`, not the row
 * columns — so the row selection is emitted under `data {}` alongside
 * `total`. A flat selection would be rejected by the server's schema.
 *
 * @param table - The GraphQL table name to query.
 * @param options - Filter, sort, pagination, and field-selection options.
 * @returns The document selecting `total` and `data { <fields> }`, and its
 *   variables (`{ filter }` when a filter is set, else `{}`).
 *
 * @example
 * ```ts
 * const { query, variables } = buildGraphqlQuery('users', {
 *   filter: { active: true },
 *   sort: [{ field: 'name', direction: 'asc' }],
 *   pagination: { limit: 25 },
 *   fields: ['id', 'name', 'email'],
 * });
 * // query: query ($filter: TableFilterusersInput) {
 * //   users(filter: $filter, sort: [name_asc], limit: 25) { total data { id name email } } }
 * // variables: { filter: { active: { _eq: true } } }
 * ```
 */
export function buildGraphqlQuery(
  table: string,
  options: QueryOptions = {},
): GraphqlRequest {
  const { filter, sort, pagination, fields } = options;
  assertGraphqlName(table, 'table');

  const filterInput = filter ? buildFilterInput(filter) : undefined;
  const hasFilter =
    filterInput !== undefined && Object.keys(filterInput).length > 0;

  const args: string[] = [];
  if (hasFilter) args.push('filter: $filter');
  if (sort && sort.length > 0) args.push(buildSortArgs(sort));
  if (pagination) {
    const pag = buildPaginationArgs(pagination.limit, pagination.offset);
    if (pag) args.push(pag);
  }

  const argStr = args.length > 0 ? `(${args.join(', ')})` : '';
  const head = hasFilter
    ? `query ($filter: ${tableFilterTypeName(table)}) `
    : '';
  const variables: Record<string, unknown> = hasFilter
    ? { filter: filterInput }
    : {};

  if (!fields || fields.length === 0) {
    return {
      query: `${head}{
  ${table}${argStr} {
    total
  }
}`,
      variables,
    };
  }

  const fieldStr = fields
    .map((field) => {
      assertGraphqlName(field, 'selection field');
      return field;
    })
    .join('\n      ');

  return {
    query: `${head}{
  ${table}${argStr} {
    total
    data {
      ${fieldStr}
    }
  }
}`,
    variables,
  };
}
