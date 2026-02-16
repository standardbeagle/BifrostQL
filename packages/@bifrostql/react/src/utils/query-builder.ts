import type { QueryOptions, TableFilter, SortOption } from '../types';

function buildFilterArgs(filter: TableFilter): string {
  const entries = Object.entries(filter);
  if (entries.length === 0) return '';

  const parts = entries.map(([field, value]) => {
    if (value === null) return `${field}: { _null: true }`;
    if (typeof value !== 'object')
      return `${field}: { _eq: ${JSON.stringify(value)} }`;

    const ops = Object.entries(value as Record<string, unknown>)
      .map(([op, val]) => `${op}: ${JSON.stringify(val)}`)
      .join(', ');
    return `${field}: { ${ops} }`;
  });

  return `filter: { ${parts.join(', ')} }`;
}

function buildSortArgs(sort: SortOption[]): string {
  if (sort.length === 0) return '';
  const parts = sort.map((s) => `"${s.field} ${s.direction}"`);
  return `sort: [${parts.join(', ')}]`;
}

function buildPaginationArgs(limit?: number, offset?: number): string {
  const parts: string[] = [];
  if (limit !== undefined) parts.push(`limit: ${limit}`);
  if (offset !== undefined) parts.push(`offset: ${offset}`);
  return parts.join(', ');
}

export function buildGraphqlQuery(
  table: string,
  options: QueryOptions = {},
): string {
  const { filter, sort, pagination, fields } = options;

  const args: string[] = [];
  if (filter) args.push(buildFilterArgs(filter));
  if (sort && sort.length > 0) args.push(buildSortArgs(sort));
  if (pagination) {
    const pag = buildPaginationArgs(pagination.limit, pagination.offset);
    if (pag) args.push(pag);
  }

  const argStr = args.length > 0 ? `(${args.join(', ')})` : '';
  const fieldStr =
    fields && fields.length > 0 ? fields.join('\n    ') : '__typename';

  return `{
  ${table}${argStr} {
    ${fieldStr}
  }
}`;
}
