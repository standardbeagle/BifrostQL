import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { PathProvider } from '@standardbeagle/virtual-router';
import { AppShellProvider } from '@bifrostql/app-shell';
import { Dashboard } from './dashboard';
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

/** Records the GraphQL request bodies the dashboard issues. */
let graphqlRequests: Array<{ query: string; variables: unknown }>;

/**
 * Builds a fetch mock that answers the session + app-metadata probes and then
 * routes each GraphQL query to a canned table response keyed by the table name
 * it selects. Each dashboard card issues its own `useBifrostQuery`, so the mock
 * inspects the query string to decide which fixture rows to return.
 */
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
        json: () => Promise.resolve({ entities: {} }),
      } as Response);
    }

    const body = init?.body
      ? (JSON.parse(init.body as string) as {
          query: string;
          variables: unknown;
        })
      : { query: '', variables: undefined };
    if (init?.body) {
      graphqlRequests.push(body);
    }

    // Two active members, three open invoices totalling 15000 cents, one
    // upcoming renewal, four attendance rows — one canned answer per table.
    let data: Record<string, unknown> = {};
    if (body.query.includes('main_members')) {
      data = { main_members: [{ id: 1 }, { id: 2 }] };
    } else if (body.query.includes('main_dues_invoices')) {
      const includesAmount = body.query.includes('amount_cents');
      data = {
        main_dues_invoices: [
          { id: 1, ...(includesAmount ? { amount_cents: 5000 } : {}) },
          { id: 2, ...(includesAmount ? { amount_cents: 5000 } : {}) },
          { id: 3, ...(includesAmount ? { amount_cents: 5000 } : {}) },
        ],
      };
    } else if (body.query.includes('main_member_memberships')) {
      data = { main_member_memberships: [{ id: 9 }] };
    } else if (body.query.includes('main_event_attendance')) {
      data = {
        main_event_attendance: [
          { id: 1 },
          { id: 2 },
          { id: 3 },
          { id: 4 },
        ],
      };
    }

    return Promise.resolve({
      ok: true,
      status: 200,
      statusText: 'OK',
      json: () => Promise.resolve({ data }),
    } as Response);
  });
}

function renderDashboard() {
  return render(
    <PathProvider path="/dashboard">
      <AppShellProvider config={{ endpoint: ENDPOINT }}>
        <Dashboard />
      </AppShellProvider>
    </PathProvider>,
  );
}

describe('Dashboard', () => {
  let originalFetch: typeof globalThis.fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it('renders the membership count card from a mocked Bifrost response', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']));

    // Act
    renderDashboard();

    // Assert
    await waitFor(() =>
      expect(screen.getByTestId('dashboard-members-card')).toHaveTextContent(
        '2',
      ),
    );
  });

  it('renders the unpaid dues count card from a mocked Bifrost response', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']));

    // Act
    renderDashboard();

    // Assert
    await waitFor(() =>
      expect(screen.getByTestId('dashboard-dues-card')).toHaveTextContent('3'),
    );
  });

  it('renders the upcoming renewals count card from a mocked Bifrost response', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']));

    // Act
    renderDashboard();

    // Assert
    await waitFor(() =>
      expect(
        screen.getByTestId('dashboard-renewals-card'),
      ).toHaveTextContent('1'),
    );
  });

  it('renders the recent attendance count card from a mocked Bifrost response', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']));

    // Act
    renderDashboard();

    // Assert
    await waitFor(() =>
      expect(
        screen.getByTestId('dashboard-attendance-card'),
      ).toHaveTextContent('4'),
    );
  });

  it('shows the dues total for a finance session and queries amount_cents', async () => {
    // Arrange: a session WITH main.members.finance.
    globalThis.fetch = createFetchMock(
      identityWith(['main.members.read', MEMBERS_FINANCE]),
    );

    // Act
    renderDashboard();

    // Assert: the dues card shows the summed amount (15000 cents = $150.00).
    await waitFor(() =>
      expect(screen.getByTestId('dashboard-dues-card')).toHaveTextContent(
        '$150.00',
      ),
    );
  });

  it('hides the dues total and never queries amount_cents for a non-finance session', async () => {
    // Arrange: a session WITHOUT main.members.finance.
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']));

    // Act
    renderDashboard();
    await waitFor(() =>
      expect(screen.getByTestId('dashboard-dues-card')).toHaveTextContent('3'),
    );

    // Assert: no dollar amount rendered, and no GraphQL query named the
    // policy-read-denied amount_cents column.
    expect(screen.getByTestId('dashboard-dues-card')).not.toHaveTextContent(
      '$',
    );
    expect(
      graphqlRequests.find((r) => /amount_cents/.test(r.query)),
    ).toBeUndefined();
  });

  it('links each card to its matching report screen', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']));

    // Act
    renderDashboard();

    // Assert
    await waitFor(() =>
      expect(screen.getByTestId('dashboard')).toBeInTheDocument(),
    );
    expect(
      screen
        .getByTestId('dashboard-dues-card')
        .querySelector('a[href="#/reports/unpaid-dues"]'),
    ).not.toBeNull();
    expect(
      screen
        .getByTestId('dashboard-renewals-card')
        .querySelector('a[href="#/reports/upcoming-renewals"]'),
    ).not.toBeNull();
    expect(
      screen
        .getByTestId('dashboard-attendance-card')
        .querySelector('a[href="#/reports/attendance-by-event"]'),
    ).not.toBeNull();
  });
});
