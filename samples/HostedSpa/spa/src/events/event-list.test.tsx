import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { PathProvider } from '@standardbeagle/virtual-router';
import { AppShellProvider } from '@bifrostql/app-shell';
import type { AppMetadata } from '@bifrostql/app-shell';
import { EventList } from './event-list';

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
 * `membership-manager.appmetadata.json`: the `main.events` entity with a
 * `title` display field, schedule fields, and admin-only audit fields.
 */
const sampleMetadata: AppMetadata = {
  entities: {
    'main.events': {
      label: 'Events',
      displayFields: ['title'],
      fields: {
        title: { widget: 'text', group: 'Identity' },
        location: { widget: 'text', group: 'Schedule' },
        starts_at: { widget: 'datepicker', group: 'Schedule' },
        ends_at: { widget: 'datepicker', group: 'Schedule' },
        capacity: { widget: 'number', group: 'Schedule' },
        tenant_id: {
          widget: 'fk-lookup',
          group: 'Admin',
          visible: false,
          readOnly: true,
        },
      },
      grid: {
        defaultColumns: ['title', 'location', 'starts_at', 'capacity'],
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
            main_events: [
              {
                id: 3,
                title: 'Spring Picnic',
                location: 'Riverside Park',
                starts_at: '2026-06-01',
                capacity: 50,
              },
            ],
          },
        }),
    } as Response);
  });
}

function renderEventList() {
  return render(
    <PathProvider path="/events">
      <AppShellProvider config={{ endpoint: ENDPOINT }}>
        <EventList />
      </AppShellProvider>
    </PathProvider>,
  );
}

describe('EventList', () => {
  let originalFetch: typeof globalThis.fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it('renders a metadata-driven event roster', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']));

    // Act
    renderEventList();

    // Assert: the event row is rendered from overlay-driven columns.
    await waitFor(() =>
      expect(screen.getByTestId('event-list')).toBeInTheDocument(),
    );
    await waitFor(() =>
      expect(screen.getByText('Spring Picnic')).toBeInTheDocument(),
    );
  });

  it('offers a New event action to a writer', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.write']));

    // Act
    renderEventList();

    // Assert
    await waitFor(() =>
      expect(
        screen.getByRole('button', { name: 'New event' }),
      ).toBeInTheDocument(),
    );
  });

  it('hides the New event action from a read-only member', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']));

    // Act
    renderEventList();

    // Assert
    await waitFor(() =>
      expect(screen.getByTestId('event-list')).toBeInTheDocument(),
    );
    expect(
      screen.queryByRole('button', { name: 'New event' }),
    ).not.toBeInTheDocument();
  });

  it('shows a missing-entity message when the overlay omits events', async () => {
    // Arrange: an overlay with no `main.events` entity.
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']), {
      entities: {},
    });

    // Act
    renderEventList();

    // Assert
    await waitFor(() =>
      expect(screen.getByTestId('event-list-missing')).toBeInTheDocument(),
    );
  });
});
