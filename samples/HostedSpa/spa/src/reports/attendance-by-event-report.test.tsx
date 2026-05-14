import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { PathProvider } from '@standardbeagle/virtual-router';
import { AppShellProvider } from '@bifrostql/app-shell';
import type { AppMetadata } from '@bifrostql/app-shell';
import { AttendanceByEventReport } from './attendance-by-event-report';

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
 * `main.event_attendance` entity with its event/member FKs and check-in time.
 */
const sampleMetadata: AppMetadata = {
  entities: {
    'main.event_attendance': {
      label: 'Event Attendance',
      displayFields: ['checked_in_at'],
      fields: {
        event_id: { widget: 'fk-lookup', group: 'Attendance' },
        member_id: { widget: 'fk-lookup', group: 'Attendance' },
        checked_in_at: { widget: 'datepicker', group: 'Attendance', readOnly: true },
        tenant_id: {
          widget: 'fk-lookup',
          group: 'Admin',
          visible: false,
          readOnly: true,
        },
      },
      grid: {
        defaultColumns: ['event_id', 'member_id', 'checked_in_at'],
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
            main_event_attendance: [
              {
                id: 7,
                event_id: 1,
                member_id: 2,
                checked_in_at: '2026-05-01T18:00:00Z',
              },
            ],
          },
        }),
    } as Response);
  });
}

function renderReport() {
  return render(
    <PathProvider path="/reports/attendance-by-event">
      <AppShellProvider config={{ endpoint: ENDPOINT }}>
        <AttendanceByEventReport />
      </AppShellProvider>
    </PathProvider>,
  );
}

describe('AttendanceByEventReport', () => {
  let originalFetch: typeof globalThis.fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it('renders the metadata-driven attendance-by-event table', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']));

    // Act
    renderReport();

    // Assert
    await waitFor(() =>
      expect(
        screen.getByTestId('attendance-by-event-report'),
      ).toBeInTheDocument(),
    );
    expect(screen.getByTestId('bifrost-table')).toBeInTheDocument();
    expect(
      screen.getByRole('heading', { name: 'Attendance by Event' }),
    ).toBeInTheDocument();
  });

  it('queries event_attendance sorted by event_id', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']));

    // Act
    renderReport();

    // Assert
    await waitFor(() => {
      const query = graphqlRequests.find((r) =>
        r.query.includes('main_event_attendance'),
      );
      expect(query).toBeDefined();
      expect(query!.query).toContain('event_id_asc');
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
