import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { PathProvider } from '@standardbeagle/virtual-router';
import { AppShellProvider } from '@bifrostql/app-shell';
import type { AppMetadata } from '@bifrostql/app-shell';
import { UnpaidDuesReport } from './unpaid-dues-report';
import { MEMBERS_FINANCE } from '../membership-plans/finance-fields';

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

/**
 * Overlay shaped like the committed `membership-manager.appmetadata.json`: the
 * `main.dues_invoices` entity with its billing fields and `status` flag.
 */
const sampleMetadata: AppMetadata = {
  entities: {
    'main.dues_invoices': {
      label: 'Dues Invoices',
      displayFields: ['member_id'],
      fields: {
        member_id: { widget: 'fk-lookup', group: 'Billing' },
        amount_cents: { widget: 'number', group: 'Billing' },
        issued_on: { widget: 'date', group: 'Billing' },
        due_on: { widget: 'date', group: 'Billing' },
        status: { widget: 'select', group: 'Billing' },
        tenant_id: {
          widget: 'fk-lookup',
          group: 'Admin',
          visible: false,
          readOnly: true,
        },
      },
      grid: {
        defaultColumns: ['member_id', 'amount_cents', 'due_on', 'status'],
      },
    },
  },
};

/** Records the GraphQL request bodies the table issues. */
let graphqlRequests: Array<{ query: string; variables: unknown }>;

function createFetchMock(identity: TestIdentity | null) {
  graphqlRequests = [];
  return vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === 'string' ? input : input.toString();

    if (url.includes('/auth/session')) {
      if (identity === null) {
        return Promise.resolve({
          ok: false,
          status: 401,
          statusText: 'Unauthorized',
          json: () => Promise.resolve(null),
        } as Response);
      }
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
        json: () => Promise.resolve(sampleMetadata),
      } as Response);
    }

    if (init?.body) {
      graphqlRequests.push(
        JSON.parse(init.body as string) as {
          query: string;
          variables: unknown;
        },
      );
    }
    return Promise.resolve({
      ok: true,
      status: 200,
      statusText: 'OK',
      json: () =>
        Promise.resolve({
          data: {
            main_dues_invoices: [
              {
                id: 7,
                member_id: 2,
                amount_cents: 5000,
                due_on: '2026-06-01',
                status: 'open',
              },
            ],
          },
        }),
    } as Response);
  });
}

function renderReport() {
  return render(
    <PathProvider path="/reports/unpaid-dues">
      <AppShellProvider config={{ endpoint: ENDPOINT }}>
        <UnpaidDuesReport />
      </AppShellProvider>
    </PathProvider>,
  );
}

describe('UnpaidDuesReport', () => {
  let originalFetch: typeof globalThis.fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it('renders the metadata-driven unpaid dues table', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']));

    // Act
    renderReport();

    // Assert
    await waitFor(() =>
      expect(screen.getByTestId('unpaid-dues-report')).toBeInTheDocument(),
    );
    expect(screen.getByTestId('bifrost-table')).toBeInTheDocument();
    expect(
      screen.getByRole('heading', { name: 'Unpaid Dues' }),
    ).toBeInTheDocument();
  });

  it('queries dues_invoices filtered to open invoices only', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']));

    // Act
    renderReport();

    // Assert
    await waitFor(() => {
      const query = graphqlRequests.find((r) =>
        r.query.includes('main_dues_invoices'),
      );
      expect(query).toBeDefined();
      expect(query!.query).toContain('status');
      expect(query!.query).toContain('open');
    });
  });

  it('hides the amount_cents column and never queries it for a non-finance session', async () => {
    // Arrange: a session WITHOUT main.members.finance.
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']));

    // Act
    renderReport();
    await waitFor(() =>
      expect(screen.getByTestId('bifrost-table')).toBeInTheDocument(),
    );

    // Assert: no amount_cents column header, non-finance columns remain.
    await waitFor(() =>
      expect(
        screen.queryByRole('columnheader', { name: /amount_cents/i }),
      ).not.toBeInTheDocument(),
    );
    expect(
      screen.getByRole('columnheader', { name: /status/i }),
    ).toBeInTheDocument();
    // No GraphQL query names the policy-read-denied column.
    expect(
      graphqlRequests.find((r) => /amount_cents/.test(r.query)),
    ).toBeUndefined();
  });

  it('shows the amount_cents column for a finance session', async () => {
    // Arrange: a session WITH main.members.finance.
    globalThis.fetch = createFetchMock(
      identityWith(['main.members.read', MEMBERS_FINANCE]),
    );

    // Act
    renderReport();
    await waitFor(() =>
      expect(screen.getByTestId('bifrost-table')).toBeInTheDocument(),
    );

    // Assert: the amount_cents column header is present.
    await waitFor(() =>
      expect(
        screen.getByRole('columnheader', { name: /amount_cents/i }),
      ).toBeInTheDocument(),
    );
  });

  it('renders no row actions — the report is read-only', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(
      identityWith(['main.members.read', 'main.members.write']),
    );

    // Act
    renderReport();

    // Assert
    await waitFor(() =>
      expect(screen.getByTestId('bifrost-table')).toBeInTheDocument(),
    );
    expect(screen.queryByText('Deactivate')).not.toBeInTheDocument();
    expect(screen.queryByText('View')).not.toBeInTheDocument();
  });
});
