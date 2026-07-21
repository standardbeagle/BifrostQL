import { useEffect, useState, type ReactNode } from 'react';
import { exportAllRows, type GraphQLFetcher } from '@standardbeagle/edit-db';
import { parseReportDefinition, reportTotalKey, type ReportDefinition, type ReportTotal } from './report-definition';

export interface ReportData {
  rows: Record<string, unknown>[];
  /** One server aggregate map per group band, keyed by that band's columns. */
  bandTotals: Map<string, Record<string, unknown>>[];
  /** Compatibility alias for the deepest band's aggregate map. */
  groupTotals: Map<string, Record<string, unknown>>;
  grandTotals: Record<string, unknown>;
}

const opField = (total: ReportTotal) => `_${total.op}`;
const aggregateSelection = (totals: readonly ReportTotal[]) => [...new Set(totals.map((total) => total.op === 'count' ? '_count' : `${opField(total)} { ${total.column} }`))].join(' ');
const aggregateValue = (row: Record<string, unknown>, total: ReportTotal) => total.op === 'count' ? row._count : (row[opField(total)] as Record<string, unknown> | undefined)?.[total.column!];
const sourceOf = (definition: ReportDefinition) => definition.source;

function filterArgs(definition: ReportDefinition): { params: string; args: string; variables: Record<string, unknown> } {
  const source = sourceOf(definition);
  return source.filter ? { params: `$filter: ${source.filterType}`, args: 'filter: $filter, ', variables: { filter: source.filter } } : { params: '', args: '', variables: {} };
}

function pageQuery(definition: ReportDefinition): string {
  const source = sourceOf(definition);
  const filter = filterArgs(definition);
  return `query ReportPage($offset: Int!, $limit: Int!${filter.params ? `, ${filter.params}` : ''}) { ${source.table}(${filter.args}offset: $offset, limit: $limit) { total data { ${definition.columns.map((column) => column.column).join(' ')} } } }`;
}

function aggregateQuery(definition: ReportDefinition): string {
  const source = sourceOf(definition);
  const filter = filterArgs(definition);
  const bands = definition.groupBands ?? [];
  const aggregates = bands.map((band, index) => {
    const groupColumns = bands.slice(0, index + 1).map((item) => item.column);
    const args = [filter.args.replace(/, $/, ''), `groupBy: [${groupColumns.join(', ')}]`].filter(Boolean).join(', ');
    return `band${index}: ${source.table}Aggregate(${args}) { ${[...groupColumns, aggregateSelection(band.totals ?? [])].filter(Boolean).join(' ')} }`;
  });
  const grandSelection = aggregateSelection(definition.grandTotals ?? []);
  const variables = filter.params ? `(${filter.params})` : '';
  return `query ReportTotals${variables} { ${aggregates.join(' ')} grand: ${source.table}Aggregate(${filter.args.replace(/, $/, '')}) { ${grandSelection} } }`;
}

async function fetchRows(fetcher: GraphQLFetcher, definition: ReportDefinition, pageSize: number): Promise<Record<string, unknown>[]> {
  const source = sourceOf(definition);
  const filter = filterArgs(definition);
  const rows: Record<string, unknown>[] = [];
  for (let offset = 0; ; ) {
    const response = await fetcher.query<Record<string, { total: number; data: Record<string, unknown>[] }>>(pageQuery(definition), { offset, limit: pageSize, ...filter.variables });
    const page = response[source.table];
    rows.push(...(page?.data ?? []));
    if (!page || rows.length >= page.total || page.data.length === 0) return rows;
    offset += page.data.length;
  }
}

/** Fetch detail pages and totals independently. Totals are never calculated from detail rows. */
export async function runReport(fetcher: GraphQLFetcher, definitionInput: ReportDefinition, pageSize = definitionInput.pageSize ?? 500): Promise<ReportData> {
  const definition = parseReportDefinition(definitionInput);
  if (!definition) throw new Error('Invalid report definition.');
  const filter = filterArgs(definition);
  const aggregate = await fetcher.query<Record<string, Record<string, unknown>[]>>(aggregateQuery(definition), filter.variables);
  const bands = definition.groupBands ?? [];
  const bandTotals = bands.map((band, index) => {
    const columns = bands.slice(0, index + 1).map((item) => item.column);
    const totals = new Map<string, Record<string, unknown>>();
    for (const row of aggregate[`band${index}`] ?? []) {
      const values: Record<string, unknown> = {};
      for (const total of band.totals ?? []) values[reportTotalKey(total)] = aggregateValue(row, total);
      totals.set(columns.map((column) => String(row[column] ?? '')).join('\0'), values);
    }
    return totals;
  });
  const groupTotals = bandTotals[bandTotals.length - 1] ?? new Map<string, Record<string, unknown>>();
  const grandTotals: Record<string, unknown> = {};
  for (const total of definition.grandTotals ?? []) grandTotals[reportTotalKey(total)] = aggregateValue(aggregate.grand?.[0] ?? {}, total);
  return { rows: await fetchRows(fetcher, definition, pageSize), bandTotals, groupTotals, grandTotals };
}

/** Full-result CSV export; the shared exporter owns paging and RFC4180 escaping. */
export async function buildReportCsv(fetcher: GraphQLFetcher, definitionInput: ReportDefinition, pageSize = definitionInput.pageSize ?? 500): Promise<string> {
  const definition = parseReportDefinition(definitionInput);
  if (!definition) throw new Error('Invalid report definition.');
  const source = sourceOf(definition);
  const filter = filterArgs(definition);
  const result = await exportAllRows({
    headers: definition.columns.map((column) => column.label ?? column.column), format: 'csv', pageSize,
    fetchPage: async (offset, limit) => {
      const response = await fetcher.query<Record<string, { total: number; data: Record<string, unknown>[] }>>(pageQuery(definition), { offset, limit, ...filter.variables });
      const page = response[source.table];
      return { total: page.total, rows: page.data.map((row) => definition.columns.map((column) => row[column.column])) };
    },
  });
  return result.content;
}

export function ReportRunner({ definition, fetcher }: { definition: ReportDefinition; fetcher: GraphQLFetcher }) {
  const [data, setData] = useState<ReportData | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { void runReport(fetcher, definition).then(setData).catch((reason) => setError(String(reason))); }, [fetcher, definition]);
  if (error) return <div role="alert">{error}</div>;
  if (!data) return <div>Running report…</div>;
  const bands = definition.groupBands ?? [];
  const groupKey = (row: Record<string, unknown>, depth: number) => bands.slice(0, depth + 1).map((band) => String(row[band.column] ?? '')).join('\0');
  const subtotal = (row: Record<string, unknown>, depth: number): ReactNode => {
    const band = bands[depth];
    const values = data.bandTotals[depth]?.get(groupKey(row, depth)) ?? {};
    return <tr className="bifrost-report__subtotal" key={`subtotal-${depth}-${groupKey(row, depth)}`}><th colSpan={definition.columns.length}>{band.column}: {String(row[band.column] ?? '')} subtotal {Object.entries(values).map(([key, value]) => `${key}: ${String(value ?? '')}`).join(' ')}</th></tr>;
  };
  const body: ReactNode[] = [];
  let previous: Record<string, unknown> | null = null;
  for (const [index, row] of data.rows.entries()) {
    const changed = previous ? bands.findIndex((band) => row[band.column] !== previous![band.column]) : 0;
    if (previous && changed >= 0) for (let depth = bands.length - 1; depth >= changed; depth--) body.push(subtotal(previous, depth));
    if (changed >= 0) for (let depth = changed; depth < bands.length; depth++) body.push(<tr className="bifrost-report__band" key={`band-${depth}-${groupKey(row, depth)}-header`}><th colSpan={definition.columns.length}>{bands[depth].column}: {String(row[bands[depth].column] ?? '')}</th></tr>);
    body.push(<tr key={`row-${index}`}>{definition.columns.map((column) => <td key={column.column}>{String(row[column.column] ?? '')}</td>)}</tr>);
    previous = row;
  }
  if (previous) for (let depth = bands.length - 1; depth >= 0; depth--) body.push(subtotal(previous, depth));
  return <section className="bifrost-report">
    <header><h2>{definition.pageHeader?.title ?? 'Report'}</h2><button type="button" onClick={() => window.print()}>Print</button></header>
    <table><thead><tr>{definition.columns.map((column) => <th key={column.column}>{column.label ?? column.column}</th>)}</tr></thead><tbody>
      {body}
    </tbody></table>
    <footer>{Object.entries(data.grandTotals).map(([key, value]) => <span key={key}>{key}: {String(value ?? '')} </span>)}</footer>
  </section>;
}
