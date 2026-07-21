import { useEffect, useState } from 'react';
import type { GraphQLFetcher, SavedObject } from '@standardbeagle/edit-db';
import { ReportRunner } from './report-runner';
import { openReport, reportStore, REPORT_TYPE } from './report-store';

/** Saved report list and runner. The injected fetcher is the shell's selected transport. */
export function ReportsPane({ fetcher }: { fetcher: GraphQLFetcher }) {
  const [reports, setReports] = useState<SavedObject[]>([]);
  const [selected, setSelected] = useState<SavedObject | null>(null);
  useEffect(() => { void reportStore.list(REPORT_TYPE).then(setReports); }, []);
  const definition = selected ? openReport(selected) : null;
  return <div className="bifrost-reports-pane"><nav aria-label="Saved reports"><h2>Reports</h2>{reports.map((report) => <button type="button" key={report.id} onClick={() => setSelected(report)}>{report.name}</button>)}</nav>
    <main>{definition ? <ReportRunner definition={definition} fetcher={fetcher} /> : <p>Select a saved report to run it.</p>}</main></div>;
}
