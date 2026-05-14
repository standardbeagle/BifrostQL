import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PathProvider, Routes, Route } from '@standardbeagle/virtual-router';
import { AppShellProvider } from '@bifrostql/app-shell';
import type { AppMetadata } from '@bifrostql/app-shell';
import { EventForm } from './event-form';
import { buildEventFormFields, MEMBERS_ADMIN } from './event-form-fields';

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
 * `membership-manager.appmetadata.json`: the `main.events` entity with
 * identity/schedule fields and admin-only (`visible: false`) `created_at` /
 * `tenant_id` audit fields.
 */
const sampleMetadata: AppMetadata = {
  entities: {
    'main.events': {
      label: 'Events',
      displayFields: ['title'],
      fields: {
        title: { widget: 'text', group: 'Identity' },
        description: { widget: 'textarea', group: 'Identity' },
        location: { widget: 'text', group: 'Schedule' },
        starts_at: { widget: 'datepicker', group: 'Schedule' },
        ends_at: { widget: 'datepicker', group: 'Schedule' },
        capacity: { widget: 'number', group: 'Schedule' },
        created_at: {
          widget: 'datepicker',
          group: 'Admin',
          visible: false,
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
  },
};

/** Records the GraphQL request bodies the form issues. */
let graphqlRequests: Array<{ query: string; variables: unknown }>;

function createFetchMock(
  identity: TestIdentity | null,
  metadata: AppMetadata = sampleMetadata,
  eventRow: Record<string, unknown> | null = null,
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
          json: () => Promise.resolve({ data: { main_events: 1 } }),
        } as Response);
      }
    }
    return Promise.resolve({
      ok: true,
      status: 200,
      statusText: 'OK',
      json: () =>
        Promise.resolve({
          data: { main_events: eventRow ? [eventRow] : [] },
        }),
    } as Response);
  });
}

const picnicRow = {
  id: 3,
  title: 'Spring Picnic',
  description: 'Annual club picnic',
  location: 'Riverside Park',
  starts_at: '2026-06-01',
  ends_at: '2026-06-01',
  capacity: 50,
  created_at: '2026-01-01',
  tenant_id: 1,
};

/**
 * Render `EventForm` inside the same single `/events/:id` route the real
 * `App.tsx` uses, so `useParams()` resolves the `:id` route param. The sentinel
 * id `new` selects create mode.
 */
function renderForm(path: string) {
  return render(
    <PathProvider path={path}>
      <AppShellProvider config={{ endpoint: ENDPOINT }}>
        <Routes>
          <Route path="/events/:id">
            <EventForm />
          </Route>
        </Routes>
      </AppShellProvider>
    </PathProvider>,
  );
}

describe('buildEventFormFields', () => {
  const entity = sampleMetadata.entities!['main.events'];

  it('orders fields by displayFields then declaration order', () => {
    // Act
    const fields = buildEventFormFields(entity, [MEMBERS_ADMIN]);

    // Assert: title leads (displayFields), rest follow.
    expect(fields[0].name).toBe('title');
    expect(fields.map((f) => f.name)).toContain('location');
  });

  it('omits admin-only (visible:false) fields for a non-admin session', () => {
    // Act
    const fields = buildEventFormFields(entity, ['main.members.write']);

    // Assert: created_at / tenant_id are gated out.
    const names = fields.map((f) => f.name);
    expect(names).not.toContain('created_at');
    expect(names).not.toContain('tenant_id');
    expect(names).toContain('title');
  });

  it('includes admin-only fields, flagged adminOnly + readOnly, for an admin', () => {
    // Act
    const fields = buildEventFormFields(entity, [MEMBERS_ADMIN]);
    const created = fields.find((f) => f.name === 'created_at');

    // Assert
    expect(created).toBeDefined();
    expect(created!.adminOnly).toBe(true);
    expect(created!.readOnly).toBe(true);
  });
});

describe('EventForm', () => {
  let originalFetch: typeof globalThis.fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it('renders a metadata-driven create form on /events/new', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.write']));

    // Act
    renderForm('/events/new');

    // Assert: fields come from the overlay, not hardcoded.
    await waitFor(() =>
      expect(screen.getByTestId('event-form')).toBeInTheDocument(),
    );
    expect(screen.getByLabelText('title')).toBeInTheDocument();
    expect(screen.getByLabelText('location')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Create' })).toBeInTheDocument();
  });

  it('hides admin-only fields for a non-admin editing an event', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(
      identityWith(['main.members.write']),
      sampleMetadata,
      picnicRow,
    );

    // Act
    renderForm('/events/3');

    // Assert: schedule fields visible, admin-only audit fields are not.
    await waitFor(() =>
      expect(screen.getByDisplayValue('Spring Picnic')).toBeInTheDocument(),
    );
    expect(screen.queryByLabelText('created_at')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('tenant_id')).not.toBeInTheDocument();
  });

  it('issues an insert mutation on Create', async () => {
    // Arrange
    const user = userEvent.setup();
    globalThis.fetch = createFetchMock(identityWith(['main.members.write']));
    renderForm('/events/new');
    await waitFor(() =>
      expect(screen.getByTestId('event-form')).toBeInTheDocument(),
    );

    // Act
    await user.type(screen.getByLabelText('title'), 'Summer Gala');
    await user.click(screen.getByRole('button', { name: 'Create' }));

    // Assert
    await waitFor(() => {
      const mutation = graphqlRequests.find((r) =>
        /\binsert\b/.test(r.query),
      );
      expect(mutation).toBeDefined();
      const detail = (mutation!.variables as { detail: Record<string, unknown> })
        .detail;
      expect(detail.title).toBe('Summer Gala');
    });
  });

  it('issues an update mutation with edited values on Save', async () => {
    // Arrange
    const user = userEvent.setup();
    globalThis.fetch = createFetchMock(
      identityWith(['main.members.write']),
      sampleMetadata,
      picnicRow,
    );
    renderForm('/events/3');
    await waitFor(() =>
      expect(screen.getByDisplayValue('Spring Picnic')).toBeInTheDocument(),
    );

    // Act: change the location and save.
    const location = screen.getByLabelText('location');
    await user.clear(location);
    await user.type(location, 'Community Hall');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    // Assert: an update mutation carrying id + edited location was sent.
    await waitFor(() => {
      const mutation = graphqlRequests.find((r) =>
        /\bupdate\b/.test(r.query),
      );
      expect(mutation).toBeDefined();
      const detail = (mutation!.variables as { detail: Record<string, unknown> })
        .detail;
      expect(detail.id).toBe(3);
      expect(detail.location).toBe('Community Hall');
    });
  });

  it('does not offer Save to a read-only member', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(
      identityWith(['main.members.read']),
      sampleMetadata,
      picnicRow,
    );

    // Act
    renderForm('/events/3');

    // Assert
    await waitFor(() =>
      expect(screen.getByDisplayValue('Spring Picnic')).toBeInTheDocument(),
    );
    expect(
      screen.queryByRole('button', { name: 'Save' }),
    ).not.toBeInTheDocument();
  });
});
