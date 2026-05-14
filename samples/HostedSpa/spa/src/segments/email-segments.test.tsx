import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PathProvider } from '@standardbeagle/virtual-router';
import { AppShellProvider } from '@bifrostql/app-shell';
import { EmailSegments } from './email-segments';
import type { AppMetadataWithSegments } from './segment-definitions';

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
 * `main.members` entity plus a top-level `emailSegments` map declaring two
 * filter-based audiences.
 */
const sampleMetadata: AppMetadataWithSegments = {
  entities: {
    'main.members': {
      label: 'Members',
      displayFields: ['last_name'],
      fields: {
        first_name: { widget: 'text', group: 'Profile' },
        last_name: { widget: 'text', group: 'Profile' },
        email: { widget: 'text', group: 'Profile' },
        status: { widget: 'select', group: 'Profile' },
      },
      grid: {
        defaultColumns: ['first_name', 'last_name', 'email', 'status'],
      },
    },
  },
  emailSegments: {
    'active-members': {
      name: 'Active Members',
      entity: 'main.members',
      filters: ['status = active'],
    },
    'lapsed-members': {
      name: 'Lapsed Members',
      entity: 'main.members',
      filters: ['status = inactive'],
    },
  },
};

/** Records the GraphQL request bodies the table issues. */
let graphqlRequests: Array<{ query: string; variables: unknown }>;

function createFetchMock(identity: TestIdentity) {
  graphqlRequests = [];
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
            members: [
              {
                id: 3,
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

function renderScreen() {
  return render(
    <PathProvider path="/reports/email-segments">
      <AppShellProvider config={{ endpoint: ENDPOINT }}>
        <EmailSegments />
      </AppShellProvider>
    </PathProvider>,
  );
}

describe('EmailSegments', () => {
  let originalFetch: typeof globalThis.fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it('lists the email segments declared in the overlay', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']));

    // Act
    renderScreen();

    // Assert
    await waitFor(() =>
      expect(screen.getByTestId('email-segments-list')).toBeInTheDocument(),
    );
    expect(
      screen.getByTestId('email-segments-select-active-members'),
    ).toHaveTextContent('Active Members');
    expect(
      screen.getByTestId('email-segments-select-lapsed-members'),
    ).toHaveTextContent('Lapsed Members');
  });

  it('issues no GraphQL query until a segment is selected', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']));

    // Act
    renderScreen();
    await waitFor(() =>
      expect(screen.getByTestId('email-segments-list')).toBeInTheDocument(),
    );

    // Assert: the audience table is not mounted, so no member query ran.
    expect(screen.queryByTestId('email-segments-audience')).not.toBeInTheDocument();
    expect(
      graphqlRequests.find((r) => r.query.includes('members')),
    ).toBeUndefined();
  });

  it('runs the selected segment filter through the Bifrost query path and renders the audience', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']));
    const user = userEvent.setup();

    // Act
    renderScreen();
    await waitFor(() =>
      expect(
        screen.getByTestId('email-segments-select-active-members'),
      ).toBeInTheDocument(),
    );
    await user.click(
      screen.getByTestId('email-segments-select-active-members'),
    );

    // Assert: the audience table mounts and queries members filtered to the
    // segment's `status = active` clause.
    await waitFor(() =>
      expect(screen.getByTestId('email-segments-audience')).toBeInTheDocument(),
    );
    expect(screen.getByTestId('bifrost-table')).toBeInTheDocument();
    await waitFor(() => {
      const query = graphqlRequests.find((r) => r.query.includes('members'));
      expect(query).toBeDefined();
      expect(query!.query).toContain('status');
      expect(query!.query).toContain('active');
    });
  });

  it('renders no send button — the screen produces an audience list only', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']));
    const user = userEvent.setup();

    // Act
    renderScreen();
    await waitFor(() =>
      expect(
        screen.getByTestId('email-segments-select-active-members'),
      ).toBeInTheDocument(),
    );
    await user.click(
      screen.getByTestId('email-segments-select-active-members'),
    );
    await waitFor(() =>
      expect(screen.getByTestId('email-segments-audience')).toBeInTheDocument(),
    );

    // Assert: no sending affordance of any kind — no send/queue button and no
    // link routing to a compose/send screen.
    expect(
      screen.queryByRole('button', { name: /send|queue|compose/i }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('link', { name: /send|compose/i }),
    ).not.toBeInTheDocument();
  });
});
