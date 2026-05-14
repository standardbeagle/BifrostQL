import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PathProvider, Routes, Route } from '@standardbeagle/virtual-router';
import { AppShellProvider } from '@bifrostql/app-shell';
import type { AppMetadata } from '@bifrostql/app-shell';
import { EventCheckin } from './event-checkin';

const ENDPOINT = 'http://localhost:5000/graphql';
const CHECK_IN_ENDPOINT = '/workflows/membership/check-in';

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
 * `main.event_attendance` entity with `event_id` / `member_id` `fk-lookup`
 * fields and a `checked_in_at` datepicker; plus `main.events` and
 * `main.members` for the title and member-name resolution.
 */
const sampleMetadata: AppMetadata = {
  entities: {
    'main.event_attendance': {
      label: 'Event Attendance',
      displayFields: ['checked_in_at'],
      fields: {
        event_id: { widget: 'fk-lookup', group: 'Attendance' },
        member_id: { widget: 'fk-lookup', group: 'Attendance' },
        checked_in_at: {
          widget: 'datepicker',
          group: 'Attendance',
          readOnly: true,
        },
        tenant_id: {
          widget: 'fk-lookup',
          group: 'Admin',
          visible: false,
          readOnly: true,
        },
      },
    },
    'main.events': {
      label: 'Events',
      displayFields: ['title'],
      fields: {
        title: { widget: 'text' },
      },
    },
    'main.members': {
      label: 'Members',
      displayFields: ['first_name', 'last_name'],
      fields: {
        first_name: { widget: 'text' },
        last_name: { widget: 'text' },
      },
    },
  },
};

/** Records the GraphQL request bodies the component issues. */
let graphqlRequests: Array<{ query: string; variables: unknown }>;
/** Records the check-in workflow-endpoint POST bodies the component issues. */
let checkInRequests: Array<Record<string, unknown>>;

const attendanceRows = [
  { id: 31, event_id: 3, member_id: 7, checked_in_at: '2025-03-01 10:02:00' },
];

const eventRow = { id: 3, title: 'Spring Picnic' };

const memberRows = [
  { id: 7, first_name: 'Ada', last_name: 'Lovelace' },
  { id: 8, first_name: 'Grace', last_name: 'Hopper' },
  { id: 9, first_name: 'Alan', last_name: 'Turing' },
];

function createFetchMock(
  identity: TestIdentity | null,
  metadata: AppMetadata = sampleMetadata,
  checkInStatus = 200,
) {
  graphqlRequests = [];
  checkInRequests = [];
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

    if (url.includes(CHECK_IN_ENDPOINT)) {
      checkInRequests.push(
        init?.body
          ? (JSON.parse(init.body as string) as Record<string, unknown>)
          : {},
      );
      return Promise.resolve({
        ok: checkInStatus >= 200 && checkInStatus < 300,
        status: checkInStatus,
        statusText: checkInStatus === 409 ? 'Conflict' : 'OK',
        json: () => Promise.resolve(null),
      } as Response);
    }

    const body = init?.body
      ? (JSON.parse(init.body as string) as {
          query: string;
          variables: unknown;
        })
      : null;
    if (body) {
      graphqlRequests.push(body);
      if (/main_members/.test(body.query)) {
        return Promise.resolve({
          ok: true,
          status: 200,
          statusText: 'OK',
          json: () => Promise.resolve({ data: { main_members: memberRows } }),
        } as Response);
      }
      if (/main_events/.test(body.query)) {
        return Promise.resolve({
          ok: true,
          status: 200,
          statusText: 'OK',
          json: () => Promise.resolve({ data: { main_events: [eventRow] } }),
        } as Response);
      }
    }
    // The event_attendance list query.
    return Promise.resolve({
      ok: true,
      status: 200,
      statusText: 'OK',
      json: () =>
        Promise.resolve({ data: { main_event_attendance: attendanceRows } }),
    } as Response);
  });
}

/**
 * Render `EventCheckin` inside the same `/events/:id/check-in` route the real
 * `App.tsx` uses, so `useParams()` resolves the `:id` event-scope param.
 */
function renderCheckin(path: string) {
  return render(
    <PathProvider path={path}>
      <AppShellProvider config={{ endpoint: ENDPOINT }}>
        <Routes>
          <Route path="/events/:id/check-in">
            <EventCheckin />
          </Route>
        </Routes>
      </AppShellProvider>
    </PathProvider>,
  );
}

describe('EventCheckin', () => {
  let originalFetch: typeof globalThis.fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it('lists the checked-in members for the event, resolving member names', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.write']));

    // Act
    renderCheckin('/events/3/check-in');

    // Assert: the attendance row resolves the member to a display name.
    await waitFor(() =>
      expect(screen.getByTestId('event-checkin')).toBeInTheDocument(),
    );
    expect(screen.getByTestId('event-checkin-list')).toHaveTextContent(
      'Ada Lovelace',
    );
  });

  it('checks a member in via the workflow endpoint — not a raw table edit', async () => {
    // Arrange
    const user = userEvent.setup();
    globalThis.fetch = createFetchMock(identityWith(['main.members.write']));
    renderCheckin('/events/3/check-in');
    await waitFor(() =>
      expect(screen.getByTestId('event-checkin-add')).toBeInTheDocument(),
    );

    // Act: narrow the roster with the fast lookup, then tap the member button.
    await user.type(screen.getByLabelText('Find a member'), 'grace');
    await user.click(screen.getByTestId('event-checkin-button-8'));

    // Assert: a POST to the check-in workflow endpoint carrying event + member.
    await waitFor(() => expect(checkInRequests).toHaveLength(1));
    expect(checkInRequests[0]).toEqual({ eventId: 3, memberId: 8 });
    // The check-in went through the workflow endpoint, never a GraphQL mutation.
    expect(
      graphqlRequests.find((r) => /\bmutation\b/.test(r.query)),
    ).toBeUndefined();
  });

  it('excludes already-checked-in members from the lookup', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.write']));
    renderCheckin('/events/3/check-in');
    await waitFor(() =>
      expect(screen.getByTestId('event-checkin-add')).toBeInTheDocument(),
    );

    // Assert: member 7 (Ada) is already checked in, so no check-in button for
    // her; members 8 and 9 are still offered.
    expect(
      screen.queryByTestId('event-checkin-button-7'),
    ).not.toBeInTheDocument();
    expect(screen.getByTestId('event-checkin-button-8')).toBeInTheDocument();
    expect(screen.getByTestId('event-checkin-button-9')).toBeInTheDocument();
  });

  it('surfaces a conflict when the endpoint reports an already-checked-in member', async () => {
    // Arrange: the endpoint responds 409 for a duplicate check-in.
    const user = userEvent.setup();
    globalThis.fetch = createFetchMock(
      identityWith(['main.members.write']),
      sampleMetadata,
      409,
    );
    renderCheckin('/events/3/check-in');
    await waitFor(() =>
      expect(screen.getByTestId('event-checkin-add')).toBeInTheDocument(),
    );

    // Act
    await user.click(screen.getByTestId('event-checkin-button-8'));

    // Assert: the conflict is surfaced inline.
    await waitFor(() =>
      expect(screen.getByTestId('event-checkin-message')).toHaveTextContent(
        'already checked in',
      ),
    );
  });

  it('hides check-in controls for a read-only session', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']));

    // Act
    renderCheckin('/events/3/check-in');

    // Assert: the attendance list still shows, but no check-in controls.
    await waitFor(() =>
      expect(screen.getByTestId('event-checkin-list')).toHaveTextContent(
        'Ada Lovelace',
      ),
    );
    expect(
      screen.queryByTestId('event-checkin-add'),
    ).not.toBeInTheDocument();
  });

  it('shows a missing-entity message when the overlay omits event_attendance', async () => {
    // Arrange: an overlay with no `main.event_attendance` entity.
    globalThis.fetch = createFetchMock(identityWith(['main.members.write']), {
      entities: {},
    });

    // Act
    renderCheckin('/events/3/check-in');

    // Assert
    await waitFor(() =>
      expect(
        screen.getByTestId('event-checkin-missing'),
      ).toBeInTheDocument(),
    );
  });
});
