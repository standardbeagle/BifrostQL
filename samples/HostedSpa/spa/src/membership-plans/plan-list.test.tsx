import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PathProvider } from '@standardbeagle/virtual-router';
import { AppShellProvider } from '@bifrostql/app-shell';
import type { AppMetadata } from '@bifrostql/app-shell';
import { PlanList } from './plan-list';
import { MEMBERS_FINANCE } from './finance-fields';

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
 * `main.membership_plans` entity with its billing fields and `is_active` flag.
 */
const sampleMetadata: AppMetadata = {
  entities: {
    'main.membership_plans': {
      label: 'Membership Plans',
      displayFields: ['name'],
      fields: {
        name: { widget: 'text', group: 'Identity' },
        description: { widget: 'textarea', group: 'Identity' },
        billing_period: { widget: 'select', group: 'Billing' },
        price_cents: { widget: 'number', group: 'Billing' },
        is_active: { widget: 'checkbox', group: 'Billing' },
        tenant_id: {
          widget: 'fk-lookup',
          group: 'Admin',
          visible: false,
          readOnly: true,
        },
      },
      grid: {
        defaultColumns: ['name', 'billing_period', 'price_cents', 'is_active'],
      },
    },
  },
};

/** Records the GraphQL request bodies the table issues. */
let graphqlRequests: Array<{ query: string; variables: unknown }>;

function createFetchMock(
  identity: TestIdentity | null,
  metadata: AppMetadata = sampleMetadata,
) {
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
        json: () => Promise.resolve(metadata),
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
            main_membership_plans: [
              {
                id: 3,
                name: 'Annual',
                billing_period: 'annual',
                price_cents: 5000,
                is_active: true,
              },
            ],
          },
        }),
    } as Response);
  });
}

function renderPlanList() {
  return render(
    <PathProvider path="/plans">
      <AppShellProvider config={{ endpoint: ENDPOINT }}>
        <PlanList />
      </AppShellProvider>
    </PathProvider>,
  );
}

describe('PlanList', () => {
  let originalFetch: typeof globalThis.fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it('renders the metadata-driven plan table', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']));

    // Act
    renderPlanList();

    // Assert
    await waitFor(() =>
      expect(screen.getByTestId('plan-list')).toBeInTheDocument(),
    );
    expect(screen.getByTestId('bifrost-table')).toBeInTheDocument();
  });

  it('shows New plan and Deactivate actions for a writer', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(
      identityWith(['main.members.read', 'main.members.write']),
    );

    // Act
    renderPlanList();

    // Assert
    await waitFor(() =>
      expect(
        screen.getByRole('button', { name: 'New plan' }),
      ).toBeInTheDocument(),
    );
    expect(screen.getByText('Deactivate')).toBeInTheDocument();
    expect(screen.getByText('View')).toBeInTheDocument();
  });

  it('hides New plan and Deactivate for a read-only session', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']));

    // Act
    renderPlanList();

    // Assert
    await waitFor(() =>
      expect(screen.getByText('View')).toBeInTheDocument(),
    );
    expect(
      screen.queryByRole('button', { name: 'New plan' }),
    ).not.toBeInTheDocument();
    expect(screen.queryByText('Deactivate')).not.toBeInTheDocument();
  });

  it('hides the price_cents column and never queries it for a non-finance session', async () => {
    // Arrange: a session WITHOUT main.members.finance.
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']));

    // Act
    renderPlanList();
    await waitFor(() =>
      expect(screen.getByTestId('bifrost-table')).toBeInTheDocument(),
    );

    // Assert: no price_cents column header, and non-finance columns remain.
    await waitFor(() =>
      expect(
        screen.queryByRole('columnheader', { name: /price_cents/i }),
      ).not.toBeInTheDocument(),
    );
    expect(
      screen.getByRole('columnheader', { name: /billing_period/i }),
    ).toBeInTheDocument();
    // No GraphQL query names the policy-read-denied column.
    expect(
      graphqlRequests.find((r) => /price_cents/.test(r.query)),
    ).toBeUndefined();
  });

  it('shows the price_cents column for a finance session', async () => {
    // Arrange: a session WITH main.members.finance.
    globalThis.fetch = createFetchMock(
      identityWith(['main.members.read', MEMBERS_FINANCE]),
    );

    // Act
    renderPlanList();
    await waitFor(() =>
      expect(screen.getByTestId('bifrost-table')).toBeInTheDocument(),
    );

    // Assert: the price_cents column header is present.
    await waitFor(() =>
      expect(
        screen.getByRole('columnheader', { name: /price_cents/i }),
      ).toBeInTheDocument(),
    );
  });

  it('issues an update mutation setting is_active false when Deactivate clicked', async () => {
    // Arrange
    const user = userEvent.setup();
    globalThis.fetch = createFetchMock(
      identityWith(['main.members.read', 'main.members.write']),
    );
    renderPlanList();
    await waitFor(() =>
      expect(screen.getByText('Deactivate')).toBeInTheDocument(),
    );

    // Act
    await user.click(screen.getByText('Deactivate'));

    // Assert
    await waitFor(() => {
      const mutation = graphqlRequests.find((r) =>
        r.query.includes('update'),
      );
      expect(mutation).toBeDefined();
      expect(mutation!.variables).toEqual({
        detail: { id: 3, is_active: false },
      });
    });
  });
});
