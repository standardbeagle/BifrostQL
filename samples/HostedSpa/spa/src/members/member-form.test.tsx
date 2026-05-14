import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PathProvider, Routes, Route } from '@standardbeagle/virtual-router';
import { AppShellProvider } from '@bifrostql/app-shell';
import type { AppMetadata } from '@bifrostql/app-shell';
import { MemberForm } from './member-form';
import { buildMemberFormFields, MEMBERS_ADMIN } from './member-form-fields';

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
 * `membership-manager.appmetadata.json`: contact fields, a `status` select,
 * and admin-only (`visible: false`) `tenant_id` / `deleted_at` audit fields.
 */
const sampleMetadata: AppMetadata = {
  entities: {
    'main.members': {
      label: 'Members',
      displayFields: ['first_name', 'last_name'],
      fields: {
        first_name: { widget: 'text', group: 'Identity' },
        last_name: { widget: 'text', group: 'Identity' },
        email: { widget: 'text', group: 'Contact' },
        phone: { widget: 'text', group: 'Contact' },
        status: { widget: 'select', group: 'Membership' },
        joined_on: { widget: 'datepicker', group: 'Membership' },
        household_id: { widget: 'fk-lookup', group: 'Membership' },
        tenant_id: {
          widget: 'fk-lookup',
          group: 'Admin',
          visible: false,
          readOnly: true,
          helpText: 'Owning club. Admin-only, system-managed.',
        },
        deleted_at: {
          widget: 'datepicker',
          group: 'Admin',
          visible: false,
          readOnly: true,
          helpText: 'Soft-delete timestamp. Admin-only audit field.',
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
  memberRow: Record<string, unknown> | null = null,
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

    // GraphQL request from the form (single-member load or mutation).
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
          json: () =>
            Promise.resolve({ data: { main_members: 1 } }),
        } as Response);
      }
    }
    // A query: return the single member row when provided.
    return Promise.resolve({
      ok: true,
      status: 200,
      statusText: 'OK',
      json: () =>
        Promise.resolve({
          data: {
            main_members: memberRow ? [memberRow] : [],
          },
        }),
    } as Response);
  });
}

const adaRow = {
  id: 7,
  first_name: 'Ada',
  last_name: 'Lovelace',
  email: 'ada@example.com',
  phone: '555-0100',
  status: 'active',
  joined_on: '2024-01-01',
  household_id: 3,
  tenant_id: 1,
  deleted_at: null,
};

/**
 * Render `MemberForm` inside the same single `/members/:id` route the real
 * `App.tsx` uses, so `useParams()` resolves the `:id` route param. The sentinel
 * id `new` selects create mode — there is deliberately no separate
 * `/members/new` route (virtual-router's `:id` segment also matches `new`).
 */
function renderForm(path: string) {
  return render(
    <PathProvider path={path}>
      <AppShellProvider config={{ endpoint: ENDPOINT }}>
        <Routes>
          <Route path="/members/:id">
            <MemberForm />
          </Route>
        </Routes>
      </AppShellProvider>
    </PathProvider>,
  );
}

describe('buildMemberFormFields', () => {
  const entity = sampleMetadata.entities!['main.members'];

  it('orders fields by displayFields then declaration order', () => {
    // Act
    const fields = buildMemberFormFields(entity, [MEMBERS_ADMIN]);

    // Assert: first_name/last_name lead (displayFields), rest follow.
    expect(fields.slice(0, 2).map((f) => f.name)).toEqual([
      'first_name',
      'last_name',
    ]);
    expect(fields.map((f) => f.name)).toContain('email');
  });

  it('omits admin-only (visible:false) fields for a non-admin session', () => {
    // Act
    const fields = buildMemberFormFields(entity, ['main.members.write']);

    // Assert: tenant_id / deleted_at are gated out.
    const names = fields.map((f) => f.name);
    expect(names).not.toContain('tenant_id');
    expect(names).not.toContain('deleted_at');
    expect(names).toContain('email');
  });

  it('includes admin-only fields, flagged adminOnly + readOnly, for an admin', () => {
    // Act
    const fields = buildMemberFormFields(entity, [MEMBERS_ADMIN]);
    const tenant = fields.find((f) => f.name === 'tenant_id');

    // Assert
    expect(tenant).toBeDefined();
    expect(tenant!.adminOnly).toBe(true);
    expect(tenant!.readOnly).toBe(true);
  });
});

describe('MemberForm', () => {
  let originalFetch: typeof globalThis.fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it('renders a metadata-driven create form on /members/new', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.write']));

    // Act
    renderForm('/members/new');

    // Assert: fields come from the overlay, not hardcoded.
    await waitFor(() =>
      expect(screen.getByTestId('member-form')).toBeInTheDocument(),
    );
    expect(screen.getByLabelText('first_name')).toBeInTheDocument();
    expect(screen.getByLabelText('email')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Create' })).toBeInTheDocument();
  });

  it('hides admin-only fields for a non-admin editing a member', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(
      identityWith(['main.members.write']),
      sampleMetadata,
      adaRow,
    );

    // Act
    renderForm('/members/7');

    // Assert: contact fields visible, admin-only audit fields are not.
    await waitFor(() =>
      expect(screen.getByDisplayValue('Ada')).toBeInTheDocument(),
    );
    expect(screen.queryByLabelText('tenant_id')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('deleted_at')).not.toBeInTheDocument();
  });

  it('shows admin-only fields as read-only for an admin editing a member', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(
      identityWith(['main.members.write', MEMBERS_ADMIN]),
      sampleMetadata,
      adaRow,
    );

    // Act
    renderForm('/members/7');

    // Assert: the admin-only audit field is present and not editable. It is an
    // fk-lookup widget, so "not editable" surfaces as a disabled <select>.
    await waitFor(() =>
      expect(screen.getByDisplayValue('Ada')).toBeInTheDocument(),
    );
    const tenantField = screen.getByLabelText('tenant_id');
    expect(tenantField).toBeInTheDocument();
    expect(tenantField).toBeDisabled();
  });

  it('issues an update mutation with edited values on Save', async () => {
    // Arrange
    const user = userEvent.setup();
    globalThis.fetch = createFetchMock(
      identityWith(['main.members.write']),
      sampleMetadata,
      adaRow,
    );
    renderForm('/members/7');
    await waitFor(() =>
      expect(screen.getByDisplayValue('Ada')).toBeInTheDocument(),
    );

    // Act: change the phone and save.
    const phone = screen.getByLabelText('phone');
    await user.clear(phone);
    await user.type(phone, '555-9999');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    // Assert: an update mutation carrying id + edited phone was sent.
    await waitFor(() => {
      const mutation = graphqlRequests.find((r) =>
        /\bupdate\b/.test(r.query),
      );
      expect(mutation).toBeDefined();
      const detail = (mutation!.variables as { detail: Record<string, unknown> })
        .detail;
      expect(detail.id).toBe(7);
      expect(detail.phone).toBe('555-9999');
    });
  });

  it('issues an insert mutation on Create', async () => {
    // Arrange
    const user = userEvent.setup();
    globalThis.fetch = createFetchMock(identityWith(['main.members.write']));
    renderForm('/members/new');
    await waitFor(() =>
      expect(screen.getByTestId('member-form')).toBeInTheDocument(),
    );

    // Act
    await user.type(screen.getByLabelText('first_name'), 'Grace');
    await user.click(screen.getByRole('button', { name: 'Create' }));

    // Assert
    await waitFor(() => {
      const mutation = graphqlRequests.find((r) =>
        /\binsert\b/.test(r.query),
      );
      expect(mutation).toBeDefined();
      const detail = (mutation!.variables as { detail: Record<string, unknown> })
        .detail;
      expect(detail.first_name).toBe('Grace');
    });
  });

  it('deactivates the member from the form via an update mutation', async () => {
    // Arrange
    const user = userEvent.setup();
    globalThis.fetch = createFetchMock(
      identityWith(['main.members.write']),
      sampleMetadata,
      adaRow,
    );
    renderForm('/members/7');
    await waitFor(() =>
      expect(screen.getByDisplayValue('Ada')).toBeInTheDocument(),
    );

    // Act
    await user.click(screen.getByRole('button', { name: 'Deactivate' }));

    // Assert: an update mutation setting status=inactive was sent.
    await waitFor(() => {
      const mutation = graphqlRequests.find(
        (r) =>
          /\bupdate\b/.test(r.query) &&
          (r.variables as { detail: Record<string, unknown> }).detail
            .status === 'inactive',
      );
      expect(mutation).toBeDefined();
      expect(
        (mutation!.variables as { detail: Record<string, unknown> }).detail.id,
      ).toBe(7);
    });
  });

  it('does not offer Deactivate to a read-only member', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(
      identityWith(['main.members.read']),
      sampleMetadata,
      adaRow,
    );

    // Act
    renderForm('/members/7');

    // Assert
    await waitFor(() =>
      expect(screen.getByDisplayValue('Ada')).toBeInTheDocument(),
    );
    expect(
      screen.queryByRole('button', { name: 'Deactivate' }),
    ).not.toBeInTheDocument();
  });
});
