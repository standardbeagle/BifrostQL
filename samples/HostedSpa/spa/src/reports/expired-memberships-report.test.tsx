import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { PathProvider } from '@standardbeagle/virtual-router';
import { AppShellProvider } from '@bifrostql/app-shell';
import type { AppMetadata } from '@bifrostql/app-shell';
import { ExpiredMembershipsReport } from './expired-memberships-report';

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
 * `main.member_memberships` entity with its term fields and `status` flag.
 */
const sampleMetadata: AppMetadata = {
  entities: {
    'main.member_memberships': {
      label: 'Member Memberships',
      displayFields: ['member_id'],
      fields: {
        member_id: { widget: 'fk-lookup', group: 'Membership' },
        plan_id: { widget: 'fk-lookup', group: 'Membership' },
        start_date: { widget: 'date', group: 'Membership' },
        end_date: { widget: 'date', group: 'Membership' },
        status: { widget: 'select', group: 'Membership' },
        tenant_id: {
          widget: 'fk-lookup',
          group: 'Admin',
          visible: false,
          readOnly: true,
        },
      },
      grid: {
        defaultColumns: ['member_id', 'plan_id', 'end_date', 'status'],
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
            main_member_memberships: [
              {
                id: 9,
                member_id: 2,
                plan_id: 1,
                end_date: '2025-01-01',
                status: 'expired',
              },
            ],
          },
        }),
    } as Response);
  });
}

function renderReport() {
  return render(
    <PathProvider path="/reports/expired-memberships">
      <AppShellProvider config={{ endpoint: ENDPOINT }}>
        <ExpiredMembershipsReport />
      </AppShellProvider>
    </PathProvider>,
  );
}

describe('ExpiredMembershipsReport', () => {
  let originalFetch: typeof globalThis.fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it('renders the metadata-driven expired memberships table', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']));

    // Act
    renderReport();

    // Assert
    await waitFor(() =>
      expect(
        screen.getByTestId('expired-memberships-report'),
      ).toBeInTheDocument(),
    );
    expect(screen.getByTestId('bifrost-table')).toBeInTheDocument();
    expect(
      screen.getByRole('heading', { name: 'Expired Memberships' }),
    ).toBeInTheDocument();
  });

  it('queries member_memberships filtered to expired status only', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']));

    // Act
    renderReport();

    // Assert
    await waitFor(() => {
      const query = graphqlRequests.find((r) =>
        r.query.includes('main_member_memberships'),
      );
      expect(query).toBeDefined();
      expect(query!.query).toContain('status');
      expect(query!.query).toContain('expired');
    });
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
