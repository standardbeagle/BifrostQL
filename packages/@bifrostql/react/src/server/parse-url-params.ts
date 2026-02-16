import type { QueryOptions, SortOption, TableFilter } from '../types';

type SearchParams = Record<string, string | string[] | undefined>;

function firstValue(value: string | string[] | undefined): string | undefined {
  if (Array.isArray(value)) return value[0];
  return value;
}

function parseSort(raw: string): SortOption[] {
  return raw.split(',').reduce<SortOption[]>((acc, part) => {
    const trimmed = part.trim();
    if (!trimmed) return acc;

    const descending = trimmed.startsWith('-');
    const field = descending ? trimmed.slice(1) : trimmed;
    if (!field) return acc;

    acc.push({ field, direction: descending ? 'desc' : 'asc' });
    return acc;
  }, []);
}

function parseFilter(params: SearchParams): TableFilter | undefined {
  const filter: TableFilter = {};
  let hasFilter = false;

  for (const [key, raw] of Object.entries(params)) {
    if (!key.startsWith('filter[') || !key.endsWith(']')) continue;

    const inner = key.slice(7, -1);
    const dotIndex = inner.indexOf('][');
    const value = firstValue(raw);
    if (value === undefined) continue;

    if (dotIndex === -1) {
      filter[inner] = value;
      hasFilter = true;
    } else {
      const field = inner.slice(0, dotIndex);
      const op = inner.slice(dotIndex + 2);
      const parsed = parseFilterValue(value);
      const existing = filter[field];
      if (existing && typeof existing === 'object' && existing !== null) {
        (existing as Record<string, unknown>)[`_${op}`] = parsed;
      } else {
        filter[field] = { [`_${op}`]: parsed } as TableFilter[string];
      }
      hasFilter = true;
    }
  }

  return hasFilter ? filter : undefined;
}

function parseFilterValue(
  value: string,
): string | number | boolean | null | Array<string | number> {
  if (value === 'null') return null;
  if (value === 'true') return true;
  if (value === 'false') return false;

  if (value.includes(',')) {
    return value.split(',').map((v) => {
      const num = Number(v);
      return Number.isFinite(num) ? num : v;
    });
  }

  const num = Number(value);
  if (Number.isFinite(num)) return num;

  return value;
}

/**
 * Parse URL search parameters into QueryOptions for use with
 * `fetchBifrostQuery` or `useBifrostQuery`.
 *
 * Supported parameter formats:
 * - `sort` - comma-separated fields, prefix with `-` for descending
 *   e.g. `sort=-created_at,name`
 * - `limit` / `offset` - pagination numbers
 * - `filter[field]` - equality shorthand e.g. `filter[status]=active`
 * - `filter[field][op]` - operator filter e.g. `filter[age][gte]=18`
 * - `fields` - comma-separated field names
 */
export function parseTableParams(params: SearchParams): QueryOptions {
  const options: QueryOptions = {};

  const sortRaw = firstValue(params.sort);
  if (sortRaw) {
    const sort = parseSort(sortRaw);
    if (sort.length > 0) options.sort = sort;
  }

  const limitRaw = firstValue(params.limit);
  if (limitRaw !== undefined) {
    const limit = Number(limitRaw);
    if (Number.isFinite(limit) && limit > 0) {
      options.pagination = { ...options.pagination, limit };
    }
  }

  const offsetRaw = firstValue(params.offset);
  if (offsetRaw !== undefined) {
    const offset = Number(offsetRaw);
    if (Number.isFinite(offset) && offset >= 0) {
      options.pagination = { ...options.pagination, offset };
    }
  }

  const filter = parseFilter(params);
  if (filter) options.filter = filter;

  const fieldsRaw = firstValue(params.fields);
  if (fieldsRaw) {
    const fields = fieldsRaw
      .split(',')
      .map((f) => f.trim())
      .filter(Boolean);
    if (fields.length > 0) options.fields = fields;
  }

  return options;
}
