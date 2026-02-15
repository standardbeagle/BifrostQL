export interface BifrostConfig {
  endpoint: string;
  headers?: Record<string, string>;
}

export interface TableFilter {
  [field: string]: FieldFilter | string | number | boolean | null;
}

export interface FieldFilter {
  _eq?: string | number | boolean | null;
  _neq?: string | number | boolean | null;
  _gt?: string | number;
  _gte?: string | number;
  _lt?: string | number;
  _lte?: string | number;
  _in?: Array<string | number>;
  _nin?: Array<string | number>;
  _contains?: string;
  _ncontains?: string;
  _starts_with?: string;
  _ends_with?: string;
  _null?: boolean;
  _nnull?: boolean;
}

export interface PaginationOptions {
  limit?: number;
  offset?: number;
}

export interface SortOption {
  field: string;
  direction: 'asc' | 'desc';
}

export interface QueryOptions {
  filter?: TableFilter;
  sort?: SortOption[];
  pagination?: PaginationOptions;
  fields?: string[];
}
