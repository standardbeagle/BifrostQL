import type {
  QueryOptions,
  SortOption,
  AdvancedFilter,
  CompoundFilter,
} from '../types';

function isCompoundFilter(filter: AdvancedFilter): filter is CompoundFilter {
  return '_and' in filter || '_or' in filter;
}

function buildFilterObject(filter: AdvancedFilter): string {
  if (isCompoundFilter(filter)) {
    const parts: string[] = [];
    if (filter._and) {
      const inner = filter._and.map((f) => `{ ${buildFilterObject(f)} }`);
      parts.push(`_and: [${inner.join(', ')}]`);
    }
    if (filter._or) {
      const inner = filter._or.map((f) => `{ ${buildFilterObject(f)} }`);
      parts.push(`_or: [${inner.join(', ')}]`);
    }
    return parts.join(', ');
  }

  const entries = Object.entries(filter);
  if (entries.length === 0) return '';

  const parts = entries.map(([field, value]) => {
    if (value === null) return `${field}: { _null: true }`;
    if (typeof value !== 'object')
      return `${field}: { _eq: ${JSON.stringify(value)} }`;

    const ops = Object.entries(value as Record<string, unknown>)
      .map(([op, val]) => {
        if (op === '_between' && Array.isArray(val) && val.length === 2) {
          return `_gte: ${JSON.stringify(val[0])}, _lte: ${JSON.stringify(val[1])}`;
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
