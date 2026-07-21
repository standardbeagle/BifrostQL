import { useEffect, useState } from 'react';
import { exportAllRows, type GraphQLFetcher } from '@standardbeagle/edit-db';
import { parseReportDefinition, reportTotalKey, type ReportDefinition, type ReportTotal } from './report-definition';

export interface ReportData {
  rows: Record<string, unknown>[];
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
  const groupColumns = definition.groupBands?.map((band) => band.column) ?? [];
  const totals = [...(definition.groupBands ?? []).flatMap((band) => band.totals ?? []), ...(definition.grandTotals ?? [])];
  const groupSelection = [...groupColumns, aggregateSelection(totals)].filter(Boolean).join(' ');
  const groupBy = groupColumns.length ? `groupBy: [${groupColumns.join(', ')}]` : '';
  const aggregateArgs = [filter.args.replace(/, $/, ''), groupBy].filter(Boolean).join(', ');
  const grandSelection = aggregateSelection(definition.grandTotals ?? []);
  const variables = filter.params ? `(${filter.params})` : '';
  return `query ReportTotals${variables} { groups: ${source.table}Aggregate(${aggregateArgs}) { ${groupSelection} } grand: ${source.table}Aggregate(${filter.args.replace(/, $/, '')}) { ${grandSelection} } }`;
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
  const aggregate = await fetcher.query<{ groups?: Record<string, unknown>[]; grand?: Record<string, unknown>[] }>(aggregateQuery(definition), filter.variables);
  const groupColumns = definition.groupBands?.map((band) => band.column) ?? [];
  const totals = [...(definition.groupBands ?? []).flatMap((band) => band.totals ?? []), ...(definition.grandTotals ?? [])];
  const groupTotals = new Map<string, Record<string, unknown>>();
  for (const row of aggregate.groups ?? []) {
    const values: Record<string, unknown> = {};
    for (const total of totals) values[reportTotalKey(total)] = aggregateValue(row, total);
    groupTotals.set(groupColumns.map((column) => String(row[column] ?? '')).join('\0'), values);
  }
  const grandTotals: Record<string, unknown> = {};
  for (const total of definition.grandTotals ?? []) grandTotals[reportTotalKey(total)] = aggregateValue(aggregate.grand?.[0] ?? {}, total);
  return { rows: await fetchRows(fetcher, definition, pageSize), groupTotals, grandTotals };
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
  return <section className="bifrost-report">
    <header><h2>{definition.pageHeader?.title ?? 'Report'}</h2><button type="button" onClick={() => window.print()}>Print</button></header>
    <table><thead><tr>{definition.columns.map((column) => <th key={column.column}>{column.label ?? column.column}</th>)}</tr></thead><tbody>
      {data.rows.map((row, index) => <tr key={index}>{definition.columns.map((column) => <td key={column.column}>{String(row[column.column] ?? '')}</td>)}</tr>)}
    </tbody></table>
    <footer>{Object.entries(data.grandTotals).map(([key, value]) => <span key={key}>{key}: {String(value ?? '')} </span>)}</footer>
  </section>;
}
