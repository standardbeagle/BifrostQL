import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PathProvider, Routes, Route } from '@standardbeagle/virtual-router';
import { AppShellProvider } from '@bifrostql/app-shell';
import type { AppMetadata } from '@bifrostql/app-shell';
import { HouseholdForm } from './household-form';

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
 * `membership-manager.appmetadata.json`: a `main.households` entity with shared
 * address fields, an admin-only (`visible: false`) `tenant_id`, and a
 * `members` childCollection relationship.
 */
const sampleMetadata: AppMetadata = {
  entities: {
    'main.households': {
      label: 'Households',
      displayFields: ['name'],
      fields: {
        name: { widget: 'text', group: 'Identity' },
        address_line1: { widget: 'text', group: 'Address' },
        city: { widget: 'text', group: 'Address' },
        region: { widget: 'text', group: 'Address' },
        postal_code: { widget: 'text', group: 'Address' },
        tenant_id: {
          widget: 'fk-lookup',
          group: 'Admin',
          visible: false,
          readOnly: true,
          helpText: 'Owning club. Admin-only, system-managed.',
        },
      },
      relationships: {
        members: {
          label: 'Members',
          targetEntity: 'main.members',
          kind: 'childCollection',
          foreignKeyField: 'household_id',
          displayColumns: ['first_name', 'last_name', 'status'],
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
  householdRow: Record<string, unknown> | null = null,
  memberRows: Array<Record<string, unknown>> = [],
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
          json: () => Promise.resolve({ data: { main_households: 1 } }),
        } as Response);
      }
      // A query: the members child list vs. the single household load are
      // distinguished by which query name the body asks for.
      if (/main_members/.test(body.query)) {
        return Promise.resolve({
          ok: true,
          status: 200,
          statusText: 'OK',
          json: () =>
            Promise.resolve({ data: { main_members: memberRows } }),
        } as Response);
      }
    }
    return Promise.resolve({
      ok: true,
      status: 200,
      statusText: 'OK',
      json: () =>
        Promise.resolve({
          data: {
            main_households: householdRow ? [householdRow] : [],
          },
        }),
    } as Response);
  });
}

const acmeHousehold = {
  id: 4,
  name: 'Acme Household',
  address_line1: '1 Main St',
  city: 'Springfield',
  region: 'IL',
  postal_code: '62701',
  tenant_id: 1,
};

const acmeMembers = [
  { id: 7, first_name: 'Ada', last_name: 'Lovelace', status: 'active' },
  { id: 8, first_name: 'Grace', last_name: 'Hopper', status: 'active' },
];

/**
 * Render `HouseholdForm` inside the same single `/households/:id` route the
 * real `App.tsx` uses. The sentinel id `new` selects create mode.
 */
function renderForm(path: string) {
  return render(
    <PathProvider path={path}>
      <AppShellProvider config={{ endpoint: ENDPOINT }}>
        <Routes>
          <Route path="/households/:id">
            <HouseholdForm />
          </Route>
        </Routes>
      </AppShellProvider>
    </PathProvider>,
  );
}

describe('HouseholdForm', () => {
  let originalFetch: typeof globalThis.fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it('renders the shared address fields from overlay metadata on create', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.write']));

    // Act
    renderForm('/households/new');

    // Assert: address fields come from the overlay, not hardcoded.
    await waitFor(() =>
      expect(screen.getByTestId('household-form')).toBeInTheDocument(),
    );
    expect(screen.getByLabelText('name')).toBeInTheDocument();
    expect(screen.getByLabelText('address_line1')).toBeInTheDocument();
    expect(screen.getByLabelText('city')).toBeInTheDocument();
    expect(screen.getByLabelText('postal_code')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Create' })).toBeInTheDocument();
  });

  it('hides the admin-only tenant_id field for a non-admin', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(
      identityWith(['main.members.write']),
      sampleMetadata,
      acmeHousehold,
      acmeMembers,
    );

    // Act
    renderForm('/households/4');

    // Assert
    await waitFor(() =>
      expect(screen.getByDisplayValue('Acme Household')).toBeInTheDocument(),
    );
    expect(screen.queryByLabelText('tenant_id')).not.toBeInTheDocument();
  });

  it('renders the linked members for an existing household', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(
      identityWith(['main.members.write']),
      sampleMetadata,
      acmeHousehold,
      acmeMembers,
    );

    // Act
    renderForm('/households/4');

    // Assert: the childCollection relationship surfaces the household's members.
    await waitFor(() =>
      expect(screen.getByDisplayValue('Acme Household')).toBeInTheDocument(),
    );
    const linked = await screen.findByTestId('household-linked-members');
    expect(linked).toHaveTextContent('Ada');
    expect(linked).toHaveTextContent('Hopper');
  });

  it('issues an update mutation with edited address on Save', async () => {
    // Arrange
    const user = userEvent.setup();
    globalThis.fetch = createFetchMock(
      identityWith(['main.members.write']),
      sampleMetadata,
      acmeHousehold,
      acmeMembers,
    );
    renderForm('/households/4');
    await waitFor(() =>
      expect(screen.getByDisplayValue('Acme Household')).toBeInTheDocument(),
    );

    // Act
    const city = screen.getByLabelText('city');
    await user.clear(city);
    await user.type(city, 'Chicago');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    // Assert
    await waitFor(() => {
      const mutation = graphqlRequests.find((r) =>
        /\bupdate\b/.test(r.query),
      );
      expect(mutation).toBeDefined();
      const detail = (mutation!.variables as { detail: Record<string, unknown> })
        .detail;
      expect(detail.id).toBe(4);
      expect(detail.city).toBe('Chicago');
    });
  });

  it('issues an insert mutation on Create', async () => {
    // Arrange
    const user = userEvent.setup();
    globalThis.fetch = createFetchMock(identityWith(['main.members.write']));
    renderForm('/households/new');
    await waitFor(() =>
      expect(screen.getByTestId('household-form')).toBeInTheDocument(),
    );

    // Act
    await user.type(screen.getByLabelText('name'), 'New Household');
    await user.click(screen.getByRole('button', { name: 'Create' }));

    // Assert
    await waitFor(() => {
      const mutation = graphqlRequests.find((r) =>
        /\binsert\b/.test(r.query),
      );
      expect(mutation).toBeDefined();
      const detail = (mutation!.variables as { detail: Record<string, unknown> })
        .detail;
      expect(detail.name).toBe('New Household');
    });
  });

  it('renders read-only with no Save action for a session without write', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(
      identityWith(['main.members.read']),
      sampleMetadata,
      acmeHousehold,
      acmeMembers,
    );

    // Act
    renderForm('/households/4');

    // Assert
    await waitFor(() =>
      expect(screen.getByDisplayValue('Acme Household')).toBeInTheDocument(),
    );
    expect(
      screen.queryByRole('button', { name: 'Save' }),
    ).not.toBeInTheDocument();
  });
});
