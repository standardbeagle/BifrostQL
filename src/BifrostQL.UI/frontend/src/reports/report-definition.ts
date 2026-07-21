export type TotalOperation = 'count' | 'sum' | 'avg' | 'min' | 'max';
const isGraphQlName = (value: unknown): value is string => typeof value === 'string' && /^[_A-Za-z][_0-9A-Za-z]*$/.test(value);

export interface ReportTotal {
  column?: string;
  op: TotalOperation;
}

export interface ReportColumn {
  column: string;
  label?: string;
}

export interface TableReportSource {
  kind: 'table';
  table: string;
  /** Schema-derived filter type; required whenever filter is supplied. */
  filterType?: string;
  filter?: Record<string, unknown>;
}

/** A saved-query reference retains the table/filter shape used by its query. */
export interface SavedQueryReportSource extends Omit<TableReportSource, 'kind'> {
  kind: 'saved-query';
  id: string;
}

export interface ReportGroupBand {
  column: string;
  sortDir?: 'asc' | 'desc';
  totals?: ReportTotal[];
}

export interface ReportDefinition {
  source: TableReportSource | SavedQueryReportSource;
  columns: ReportColumn[];
  groupBands?: ReportGroupBand[];
  grandTotals?: ReportTotal[];
  pageHeader?: { title?: string; timestamp?: boolean; pageNumber?: boolean };
  pageFooter?: { title?: string; timestamp?: boolean; pageNumber?: boolean };
  pageSize?: number;
}

const totalOps = new Set<TotalOperation>(['count', 'sum', 'avg', 'min', 'max']);
const isRecord = (value: unknown): value is Record<string, unknown> => typeof value === 'object' && value !== null && !Array.isArray(value);

function validTotal(value: unknown): value is ReportTotal {
  if (!isRecord(value) || typeof value.op !== 'string' || !totalOps.has(value.op as TotalOperation)) return false;
  return value.op === 'count' ? value.column === undefined || isGraphQlName(value.column) : isGraphQlName(value.column);
}

/** Parse persisted report JSON and reject names that cannot safely enter GraphQL text. */
export function parseReportDefinition(value: unknown): ReportDefinition | null {
  if (!isRecord(value) || !isRecord(value.source) || !Array.isArray(value.columns)) return null;
  const source = value.source;
  if ((source.kind !== 'table' && source.kind !== 'saved-query') || !isGraphQlName(source.table)) return null;
  if (source.kind === 'saved-query' && (typeof source.id !== 'string' || source.id.length === 0)) return null;
  if (source.filter !== undefined && (!isRecord(source.filter) || !isGraphQlName(source.filterType))) return null;
  if (!value.columns.every((column) => isRecord(column) && isGraphQlName(column.column) && (column.label === undefined || typeof column.label === 'string'))) return null;
  const bands = value.groupBands;
  if (bands !== undefined && (!Array.isArray(bands) || !bands.every((band) => isRecord(band) && isGraphQlName(band.column) && (band.sortDir === undefined || band.sortDir === 'asc' || band.sortDir === 'desc') && (band.totals === undefined || Array.isArray(band.totals) && band.totals.every(validTotal))))) return null;
  if (value.grandTotals !== undefined && (!Array.isArray(value.grandTotals) || !value.grandTotals.every(validTotal))) return null;
  if (value.pageSize !== undefined && (typeof value.pageSize !== 'number' || !Number.isInteger(value.pageSize) || value.pageSize < 1 || value.pageSize > 10_000)) return null;
  return value as unknown as ReportDefinition;
}

export function reportTotalKey(total: ReportTotal): string {
  return `_${total.op}.${total.column ?? 'count'}`;
}
