import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PathProvider, Routes, Route } from '@standardbeagle/virtual-router';
import { AppShellProvider } from '@bifrostql/app-shell';
import type { AppMetadata } from '@bifrostql/app-shell';
import { EventRsvps } from './event-rsvps';

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
 * `main.event_rsvps` entity with `event_id` / `member_id` `fk-lookup` fields, a
 * `response` select and a `guests` number; plus `main.events` and
 * `main.members` for the title and fk-lookup resolution.
 */
const sampleMetadata: AppMetadata = {
  entities: {
    'main.event_rsvps': {
      label: 'Event RSVPs',
      displayFields: ['response'],
      fields: {
        event_id: { widget: 'fk-lookup', group: 'RSVP' },
        member_id: { widget: 'fk-lookup', group: 'RSVP' },
        response: { widget: 'select', group: 'RSVP' },
        guests: { widget: 'number', group: 'RSVP' },
        responded_at: { widget: 'datepicker', group: 'RSVP', readOnly: true },
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

const rsvpRows = [
  { id: 21, event_id: 3, member_id: 7, response: 'yes', guests: 2 },
  { id: 22, event_id: 3, member_id: 8, response: 'maybe', guests: 0 },
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

    const body = init?.body
      ? (JSON.parse(init.body as string) as {
          query: string;
          variables: unknown;
        })
      : null;
    if (body) {
      graphqlRequests.push(body);
      if (/\bmutation\b/.test(body.query)) {
        return Promise.resolve({
          ok: true,
          status: 200,
          statusText: 'OK',
          json: () => Promise.resolve({ data: { main_event_rsvps: 1 } }),
        } as Response);
      }
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
    // The event_rsvps list query.
    return Promise.resolve({
      ok: true,
      status: 200,
      statusText: 'OK',
      json: () => Promise.resolve({ data: { main_event_rsvps: rsvpRows } }),
    } as Response);
  });
}

/**
 * Render `EventRsvps` inside the same `/events/:id/rsvps` route the real
 * `App.tsx` uses, so `useParams()` resolves the `:id` event-scope param.
 */
function renderRsvps(path: string) {
  return render(
    <PathProvider path={path}>
      <AppShellProvider config={{ endpoint: ENDPOINT }}>
        <Routes>
          <Route path="/events/:id/rsvps">
            <EventRsvps />
          </Route>
        </Routes>
      </AppShellProvider>
    </PathProvider>,
  );
}

describe('EventRsvps', () => {
  let originalFetch: typeof globalThis.fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it('lists the RSVPs for the event, resolving member names', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.write']));

    // Act
    renderRsvps('/events/3/rsvps');

    // Assert: each RSVP row resolves the member to a display name, not a raw id.
    await waitFor(() =>
      expect(screen.getByTestId('event-rsvps')).toBeInTheDocument(),
    );
    const list = screen.getByTestId('event-rsvps-list');
    expect(list).toHaveTextContent('Ada Lovelace');
    expect(list).toHaveTextContent('Grace Hopper');
  });

  it('records a new RSVP via the fk-lookup select — no manual FK id entry', async () => {
    // Arrange
    const user = userEvent.setup();
    globalThis.fetch = createFetchMock(identityWith(['main.members.write']));
    renderRsvps('/events/3/rsvps');
    await waitFor(() =>
      expect(screen.getByTestId('event-rsvps')).toBeInTheDocument(),
    );

    // Act: pick a member from the fk-lookup <select>, set a response, record.
    const memberSelect = screen.getByLabelText('member_id');
    expect(memberSelect.tagName).toBe('SELECT');
    expect(memberSelect).toHaveTextContent('Alan Turing');
    await user.selectOptions(memberSelect, '9');
    await user.selectOptions(screen.getByLabelText('response'), 'yes');
    await user.click(screen.getByRole('button', { name: 'Record RSVP' }));

    // Assert: an insert mutation carrying event_id + selected member_id.
    await waitFor(() => {
      const mutation = graphqlRequests.find((r) =>
        /\binsert\b/.test(r.query),
      );
      expect(mutation).toBeDefined();
      const detail = (mutation!.variables as { detail: Record<string, unknown> })
        .detail;
      expect(detail.event_id).toBe('3');
      expect(detail.member_id).toBe('9');
      expect(detail.response).toBe('yes');
    });
  });

  it('edits the response of an existing RSVP via an update mutation', async () => {
    // Arrange
    const user = userEvent.setup();
    globalThis.fetch = createFetchMock(identityWith(['main.members.write']));
    renderRsvps('/events/3/rsvps');
    await waitFor(() =>
      expect(screen.getByTestId('event-rsvps')).toBeInTheDocument(),
    );

    // Act: change the response select on RSVP row 21, save.
    await user.selectOptions(
      screen.getByTestId('event-rsvp-response-21'),
      'no',
    );
    await user.click(screen.getByTestId('event-rsvp-save-21'));

    // Assert
    await waitFor(() => {
      const mutation = graphqlRequests.find((r) =>
        /\bupdate\b/.test(r.query),
      );
      expect(mutation).toBeDefined();
      const detail = (mutation!.variables as { detail: Record<string, unknown> })
        .detail;
      expect(detail.id).toBe(21);
      expect(detail.response).toBe('no');
    });
  });

  it('hides record / edit controls for a read-only session', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']));

    // Act
    renderRsvps('/events/3/rsvps');

    // Assert: the RSVPs are still listed, but no mutating controls are offered.
    await waitFor(() =>
      expect(screen.getByTestId('event-rsvps-list')).toHaveTextContent(
        'Ada Lovelace',
      ),
    );
    expect(
      screen.queryByRole('button', { name: 'Record RSVP' }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByTestId('event-rsvp-save-21'),
    ).not.toBeInTheDocument();
  });

  it('shows a missing-entity message when the overlay omits event_rsvps', async () => {
    // Arrange: an overlay with no `main.event_rsvps` entity.
    globalThis.fetch = createFetchMock(identityWith(['main.members.write']), {
      entities: {},
    });

    // Act
    renderRsvps('/events/3/rsvps');

    // Assert
    await waitFor(() =>
      expect(screen.getByTestId('event-rsvps-missing')).toBeInTheDocument(),
    );
  });
});
