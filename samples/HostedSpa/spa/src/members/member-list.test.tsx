import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PathProvider } from '@standardbeagle/virtual-router';
import { AppShellProvider } from '@bifrostql/app-shell';
import type { AppMetadata } from '@bifrostql/app-shell';
import { MemberList } from './member-list';
import { buildFilterControls, buildTableFilter } from './member-list-filters';

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
 * App-metadata overlay shaped like the committed
 * `membership-manager.appmetadata.json`: a `status` select field with a value
 * discoverable from a saved view, and `text` fields for search.
 */
const sampleMetadata: AppMetadata = {
  entities: {
    'main.members': {
      label: 'Members',
      fields: {
        first_name: { widget: 'text', group: 'Identity' },
        last_name: { widget: 'text', group: 'Identity' },
        email: { widget: 'text', group: 'Contact' },
        status: { widget: 'select', group: 'Membership' },
        household_id: { widget: 'fk-lookup', group: 'Membership' },
        tenant_id: { widget: 'fk-lookup', visible: false },
      },
      grid: {
        defaultColumns: ['first_name', 'last_name', 'email', 'status'],
        defaultFilters: ['status = active'],
        savedViews: {
          active: { name: 'Active Members', filters: ['status = active'] },
          inactive: {
            name: 'Inactive Members',
            filters: ['status = inactive'],
          },
        },
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

    // GraphQL request from BifrostTable.
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
            // BifrostQL names the non-`dbo` `main` schema's table `main_members`
            // (see `entityKeyToQueryName`).
            main_members: [
              {
                id: 7,
                first_name: 'Ada',
                last_name: 'Lovelace',
                email: 'ada@example.com',
                status: 'active',
              },
            ],
          },
        }),
    } as Response);
  });
}

function renderMemberList() {
  return render(
    <PathProvider path="/members">
      <AppShellProvider config={{ endpoint: ENDPOINT }}>
        <MemberList />
      </AppShellProvider>
    </PathProvider>,
  );
}

describe('buildFilterControls', () => {
  it('derives controls only from text/select fields in the grid preset', () => {
    // Act
    const controls = buildFilterControls(
      sampleMetadata.entities!['main.members'],
    );

    // Assert: fk-lookup household_id and hidden tenant_id are excluded.
    expect(controls.map((c) => c.field)).toEqual([
      'first_name',
      'last_name',
      'email',
      'status',
    ]);
  });

  it('makes a select control a dropdown when saved views reveal values', () => {
    // Act
    const controls = buildFilterControls(
      sampleMetadata.entities!['main.members'],
    );
    const statusControl = controls.find((c) => c.field === 'status');

    // Assert: both saved-view `status = ...` filters are discovered.
    expect(statusControl).toMatchObject({
      kind: 'select',
      options: ['active', 'inactive'],
    });
  });

  it('falls back to a text control for a select field with no discovered values', () => {
    // Arrange: a select field with no saved views / default filters.
    const controls = buildFilterControls({
      fields: { tier: { widget: 'select' } },
      grid: { defaultColumns: ['tier'] },
    });

    // Assert
    expect(controls[0]).toMatchObject({ field: 'tier', kind: 'text' });
  });
});

describe('buildTableFilter', () => {
  it('builds _contains for text controls and _eq for select controls', () => {
    // Arrange
    const controls = buildFilterControls(
      sampleMetadata.entities!['main.members'],
    );

    // Act
    const filter = buildTableFilter(controls, {
      first_name: 'Ada',
      status: 'active',
    });

    // Assert
    expect(filter).toEqual({
      first_name: { _contains: 'Ada' },
      status: { _eq: 'active' },
    });
  });

  it('omits empty and whitespace-only values', () => {
    // Arrange
    const controls = buildFilterControls(
      sampleMetadata.entities!['main.members'],
    );

    // Act
    const filter = buildTableFilter(controls, { first_name: '   ', email: '' });

    // Assert
    expect(filter).toEqual({});
  });
});

describe('MemberList', () => {
  let originalFetch: typeof globalThis.fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it('renders the member table and metadata-driven filters', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']));

    // Act
    renderMemberList();

    // Assert: list, the status select, and a text search all come from metadata.
    await waitFor(() =>
      expect(screen.getByTestId('member-list')).toBeInTheDocument(),
    );
    expect(screen.getByTestId('filter-status')).toBeInTheDocument();
    expect(screen.getByTestId('filter-first_name')).toBeInTheDocument();
    expect(screen.getByTestId('bifrost-table')).toBeInTheDocument();
  });

  it('shows the Deactivate action for a member with write permission', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(
      identityWith(['main.members.read', 'main.members.write']),
    );

    // Act
    renderMemberList();

    // Assert
    await waitFor(() =>
      expect(screen.getByText('Deactivate')).toBeInTheDocument(),
    );
    expect(screen.getByText('View')).toBeInTheDocument();
  });

  it('hides the Deactivate action for a read-only member', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']));

    // Act
    renderMemberList();

    // Assert: View is present, Deactivate is not.
    await waitFor(() =>
      expect(screen.getByText('View')).toBeInTheDocument(),
    );
    expect(screen.queryByText('Deactivate')).not.toBeInTheDocument();
  });

  it('renders a saved-view picker driven by the overlay', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']));

    // Act
    renderMemberList();

    // Assert: the picker and an option per overlay saved view are present.
    await waitFor(() =>
      expect(screen.getByTestId('saved-view-picker')).toBeInTheDocument(),
    );
    expect(screen.getByText('Active Members')).toBeInTheDocument();
    expect(screen.getByText('Inactive Members')).toBeInTheDocument();
  });

  it('changes the issued GraphQL filter when a saved view is selected', async () => {
    // Arrange
    const user = userEvent.setup();
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']));
    renderMemberList();
    await waitFor(() =>
      expect(screen.getByTestId('saved-view-picker')).toBeInTheDocument(),
    );
    // Baseline: the table query carries no status filter yet.
    await waitFor(() => expect(graphqlRequests.length).toBeGreaterThan(0));
    expect(
      graphqlRequests.some((r) => r.query.includes('status: { _eq: "inactive"')),
    ).toBe(false);

    // Act: select the Inactive Members saved view.
    await user.selectOptions(
      screen.getByTestId('saved-view-picker'),
      'inactive',
    );

    // Assert: a query carrying the saved view's filter is issued.
    await waitFor(() =>
      expect(
        graphqlRequests.some((r) =>
          r.query.includes('status: { _eq: "inactive"'),
        ),
      ).toBe(true),
    );
  });

  it('issues an update mutation when Deactivate is clicked', async () => {
    // Arrange
    const user = userEvent.setup();
    globalThis.fetch = createFetchMock(
      identityWith(['main.members.read', 'main.members.write']),
    );
    renderMemberList();
    await waitFor(() =>
      expect(screen.getByText('Deactivate')).toBeInTheDocument(),
    );

    // Act
    await user.click(screen.getByText('Deactivate'));

    // Assert: an update mutation carrying the inactive status was sent.
    await waitFor(() => {
      const mutation = graphqlRequests.find((r) =>
        r.query.includes('update'),
      );
      expect(mutation).toBeDefined();
      expect(mutation!.variables).toEqual({
        detail: { id: 7, status: 'inactive' },
      });
    });
  });
});
