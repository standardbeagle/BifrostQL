import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PathProvider } from '@standardbeagle/virtual-router';
import { AppShellProvider } from '@bifrostql/app-shell';
import type { ColumnConfig } from '@bifrostql/react';
import { ExportButton } from './export-button';
import * as csvExport from './csv-export';

const ENDPOINT = 'http://localhost:5000/graphql';

/** Identity contract mirror — see app-shell `AppIdentity`. */
interface TestIdentity {
  id: string;
  provider: string;
  email: string;
  displayName: string;
  orgIds: string[];
  roles: string[];
  permissions: string[];
  claims: Record<string, unknown>;
}

function identityWith(permissions: string[]): TestIdentity {
  return {
    id: 'user-1',
    provider: 'local',
    email: 'officer@example.com',
    displayName: 'Test Officer',
    orgIds: [],
    roles: [],
    permissions,
    claims: {},
  };
}

/** The gated column list a non-finance session passes — no `amount_cents`. */
const nonFinanceColumns: ColumnConfig[] = [
  { field: 'member_id', header: 'Member' },
  { field: 'status', header: 'Status' },
];

/** The ungated column list a finance session passes. */
const allColumns: ColumnConfig[] = [
  ...nonFinanceColumns,
  { field: 'amount_cents', header: 'Amount' },
];

/** Records the GraphQL request bodies the export issues. */
let graphqlRequests: Array<{ query: string }>;


/** Wrap rows in the server's paged envelope (`{ total, data }`). */
function paged(rows: unknown[], total = rows.length) {
  return { total, data: rows };
}

/** The row window the server applies to a query that names no `limit`. */
const SERVER_DEFAULT_ROW_WINDOW = 100;

/**
 * Builds `count` invoice rows. Row 1 is the `2,open,5000` the single-row
 * facts assert on; the rest are distinguishable by `member_id`.
 */
function invoiceRows(count: number): Array<Record<string, unknown>> {
  return Array.from({ length: count }, (_, i) => ({
    member_id: i + 2,
    status: 'open',
    amount_cents: 5000,
  }));
}

function createFetchMock(identity: TestIdentity, rowCount = 1) {
  graphqlRequests = [];
  const rows = invoiceRows(rowCount);
  return vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === 'string' ? input : input.toString();

    if (url.includes('/auth/session')) {
      return Promise.resolve({
        ok: true,
        status: 200,
        statusText: 'OK',
        json: () => Promise.resolve(identity),
      } as Response);
    }

    if (url.includes('/_app-metadata')) {
      return Promise.resolve({
        ok: true,
        status: 200,
        statusText: 'OK',
        json: () => Promise.resolve({ entities: {} }),
      } as Response);
    }

    const body = init?.body
      ? (JSON.parse(init.body as string) as { query: string })
      : { query: '' };
    if (init?.body) {
      graphqlRequests.push(body);
    }
    // Like the server: at most `limit` rows (default 100) come back, while
    // `total` carries the unpaged match count.
    const limitMatch = /\blimit:\s*(\d+)/.exec(body.query);
    const limit = limitMatch ? Number(limitMatch[1]) : SERVER_DEFAULT_ROW_WINDOW;
    return Promise.resolve({
      ok: true,
      status: 200,
      statusText: 'OK',
      json: () =>
        Promise.resolve({
          data: {
            main_dues_invoices: paged(rows.slice(0, limit), rows.length),
          },
        }),
    } as Response);
  });
}

function renderButton(columns: ColumnConfig[]) {
  return render(
    <PathProvider path="/reports/unpaid-dues">
      <AppShellProvider config={{ endpoint: ENDPOINT }}>
        <ExportButton
          queryName="main_dues_invoices"
          columns={columns}
          fileName="dues"
        />
      </AppShellProvider>
    </PathProvider>,
  );
}

describe('ExportButton', () => {
  let originalFetch: typeof globalThis.fetch;
  let downloadSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
    downloadSpy = vi
      .spyOn(csvExport, 'downloadCsv')
      .mockImplementation(() => {});
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it('issues no query until the button is clicked', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']));

    // Act
    renderButton(allColumns);

    // Assert: mounting alone issues no GraphQL request.
    await waitFor(() =>
      expect(screen.getByTestId('export-button')).toBeInTheDocument(),
    );
    expect(graphqlRequests).toHaveLength(0);
  });

  it('runs the query through the Bifrost path and triggers a CSV download on click', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']));
    const user = userEvent.setup();
    renderButton(allColumns);
    await waitFor(() =>
      expect(screen.getByTestId('export-button')).toBeInTheDocument(),
    );

    // Act
    await user.click(screen.getByTestId('export-button'));

    // Assert: query went through the Bifrost path, download was triggered.
    await waitFor(() =>
      expect(graphqlRequests.length).toBeGreaterThan(0),
    );
    await waitFor(() => expect(downloadSpy).toHaveBeenCalledOnce());
    const [csv, filename] = downloadSpy.mock.calls[0] as [string, string];
    expect(filename).toBe('dues.csv');
    expect(csv).toContain('Member,Status,Amount');
    expect(csv).toContain('2,open,5000');
  });

  it('excludes a finance column for a non-finance session', async () => {
    // Arrange: a non-finance session passes the gated column list (no
    // amount_cents), exactly as dues-report.tsx does.
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']));
    const user = userEvent.setup();
    renderButton(nonFinanceColumns);
    await waitFor(() =>
      expect(screen.getByTestId('export-button')).toBeInTheDocument(),
    );

    // Act
    await user.click(screen.getByTestId('export-button'));

    // Assert: the issued query never names the policy-read-denied column,
    // and the downloaded CSV has no Amount column.
    await waitFor(() => expect(downloadSpy).toHaveBeenCalledOnce());
    expect(
      graphqlRequests.every((r) => !/amount_cents/.test(r.query)),
    ).toBe(true);
    const [csv] = downloadSpy.mock.calls[0] as [string, string];
    expect(csv).not.toContain('Amount');
    expect(csv).toContain('Member,Status');
  });

  it('exports every matching row, not the server default 100-row window', async () => {
    // Arrange: 250 matching rows — more than a limit-less query returns.
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']), 250);
    const user = userEvent.setup();
    renderButton(nonFinanceColumns);
    await waitFor(() =>
      expect(screen.getByTestId('export-button')).toBeInTheDocument(),
    );

    // Act
    await user.click(screen.getByTestId('export-button'));

    // Assert: header + 250 data rows, the last row present, no truncation
    // notice.
    await waitFor(() => expect(downloadSpy).toHaveBeenCalledOnce());
    const [csv] = downloadSpy.mock.calls[0] as [string, string];
    const lines = csv.split('\r\n');
    expect(lines).toHaveLength(251);
    expect(lines[250]).toBe('251,open');
    expect(
      screen.queryByTestId('export-button-truncated'),
    ).not.toBeInTheDocument();
  });

  it('says so when the matching rows exceed the export cap', async () => {
    // Arrange: 10050 matching rows — past the 10000-row export cap.
    globalThis.fetch = createFetchMock(
      identityWith(['main.members.read']),
      10_050,
    );
    const user = userEvent.setup();
    renderButton(nonFinanceColumns);
    await waitFor(() =>
      expect(screen.getByTestId('export-button')).toBeInTheDocument(),
    );

    // Act
    await user.click(screen.getByTestId('export-button'));

    // Assert: the capped file is written and the screen names both counts.
    await waitFor(() => expect(downloadSpy).toHaveBeenCalledOnce());
    const [csv] = downloadSpy.mock.calls[0] as [string, string];
    expect(csv.split('\r\n')).toHaveLength(10_001);
    expect(screen.getByTestId('export-button-truncated')).toHaveTextContent(
      'Exported 10000 of 10050 rows',
    );
  });
});
