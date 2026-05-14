import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PathProvider, Routes, Route } from '@standardbeagle/virtual-router';
import { AppShellProvider } from '@bifrostql/app-shell';
import type { AppMetadata } from '@bifrostql/app-shell';
import { PlanForm } from './plan-form';
import { buildPlanFormFields, MEMBERS_ADMIN } from './plan-form-fields';

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
 * `main.membership_plans` entity with billing fields and an admin-only
 * (`visible: false`) `tenant_id`.
 */
const sampleMetadata: AppMetadata = {
  entities: {
    'main.membership_plans': {
      label: 'Membership Plans',
      displayFields: ['name'],
      fields: {
        name: { widget: 'text', group: 'Identity' },
        description: { widget: 'textarea', group: 'Identity' },
        billing_period: { widget: 'select', group: 'Billing' },
        price_cents: { widget: 'number', group: 'Billing' },
        is_active: { widget: 'checkbox', group: 'Billing' },
        tenant_id: {
          widget: 'fk-lookup',
          group: 'Admin',
          visible: false,
          readOnly: true,
          helpText: 'Owning club. Admin-only, system-managed.',
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
  planRow: Record<string, unknown> | null = null,
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
          json: () =>
            Promise.resolve({ data: { main_membership_plans: 1 } }),
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
            main_membership_plans: planRow ? [planRow] : [],
          },
        }),
    } as Response);
  });
}

const annualRow = {
  id: 3,
  name: 'Annual',
  description: 'Full year membership',
  billing_period: 'annual',
  price_cents: 5000,
  is_active: true,
  tenant_id: 1,
};

/**
 * Render `PlanForm` inside the same single `/plans/:id` route the real
 * `App.tsx` uses, so `useParams()` resolves the `:id` route param. The sentinel
 * id `new` selects create mode.
 */
function renderForm(path: string) {
  return render(
    <PathProvider path={path}>
      <AppShellProvider config={{ endpoint: ENDPOINT }}>
        <Routes>
          <Route path="/plans/:id">
            <PlanForm />
          </Route>
        </Routes>
      </AppShellProvider>
    </PathProvider>,
  );
}

describe('buildPlanFormFields', () => {
  const entity = sampleMetadata.entities!['main.membership_plans'];

  it('orders fields by displayFields then declaration order', () => {
    // Act
    const fields = buildPlanFormFields(entity, [MEMBERS_ADMIN]);

    // Assert
    expect(fields[0].name).toBe('name');
    expect(fields.map((f) => f.name)).toContain('price_cents');
  });

  it('omits admin-only (visible:false) tenant_id for a non-admin session', () => {
    // Act
    const fields = buildPlanFormFields(entity, ['main.members.write']);

    // Assert
    const names = fields.map((f) => f.name);
    expect(names).not.toContain('tenant_id');
    expect(names).toContain('price_cents');
  });

  it('includes tenant_id flagged adminOnly + readOnly for an admin', () => {
    // Act
    const fields = buildPlanFormFields(entity, [MEMBERS_ADMIN]);
    const tenant = fields.find((f) => f.name === 'tenant_id');

    // Assert
    expect(tenant).toBeDefined();
    expect(tenant!.adminOnly).toBe(true);
    expect(tenant!.readOnly).toBe(true);
  });
});

describe('PlanForm', () => {
  let originalFetch: typeof globalThis.fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it('renders a metadata-driven create form on /plans/new', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.write']));

    // Act
    renderForm('/plans/new');

    // Assert
    await waitFor(() =>
      expect(screen.getByTestId('plan-form')).toBeInTheDocument(),
    );
    expect(screen.getByLabelText('name')).toBeInTheDocument();
    expect(screen.getByLabelText('price_cents')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Create' })).toBeInTheDocument();
  });

  it('issues an insert mutation on Create', async () => {
    // Arrange
    const user = userEvent.setup();
    globalThis.fetch = createFetchMock(identityWith(['main.members.write']));
    renderForm('/plans/new');
    await waitFor(() =>
      expect(screen.getByTestId('plan-form')).toBeInTheDocument(),
    );

    // Act
    await user.type(screen.getByLabelText('name'), 'Monthly');
    await user.click(screen.getByRole('button', { name: 'Create' }));

    // Assert
    await waitFor(() => {
      const mutation = graphqlRequests.find((r) =>
        /\binsert\b/.test(r.query),
      );
      expect(mutation).toBeDefined();
      const detail = (mutation!.variables as { detail: Record<string, unknown> })
        .detail;
      expect(detail.name).toBe('Monthly');
    });
  });

  it('issues an update mutation with edited values on Save', async () => {
    // Arrange
    const user = userEvent.setup();
    globalThis.fetch = createFetchMock(
      identityWith(['main.members.write']),
      sampleMetadata,
      annualRow,
    );
    renderForm('/plans/3');
    await waitFor(() =>
      expect(screen.getByDisplayValue('Annual')).toBeInTheDocument(),
    );

    // Act
    const name = screen.getByLabelText('name');
    await user.clear(name);
    await user.type(name, 'Annual Plus');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    // Assert
    await waitFor(() => {
      const mutation = graphqlRequests.find((r) =>
        /\bupdate\b/.test(r.query),
      );
      expect(mutation).toBeDefined();
      const detail = (mutation!.variables as { detail: Record<string, unknown> })
        .detail;
      expect(detail.id).toBe(3);
      expect(detail.name).toBe('Annual Plus');
    });
  });

  it('deactivates the plan via an update mutation setting is_active false', async () => {
    // Arrange
    const user = userEvent.setup();
    globalThis.fetch = createFetchMock(
      identityWith(['main.members.write']),
      sampleMetadata,
      annualRow,
    );
    renderForm('/plans/3');
    await waitFor(() =>
      expect(screen.getByDisplayValue('Annual')).toBeInTheDocument(),
    );

    // Act
    await user.click(screen.getByRole('button', { name: 'Deactivate' }));

    // Assert
    await waitFor(() => {
      const mutation = graphqlRequests.find(
        (r) =>
          /\bupdate\b/.test(r.query) &&
          (r.variables as { detail: Record<string, unknown> }).detail
            .is_active === false,
      );
      expect(mutation).toBeDefined();
      expect(
        (mutation!.variables as { detail: Record<string, unknown> }).detail.id,
      ).toBe(3);
    });
  });

  it('does not offer Save or Deactivate to a read-only session', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(
      identityWith(['main.members.read']),
      sampleMetadata,
      annualRow,
    );

    // Act
    renderForm('/plans/3');

    // Assert
    await waitFor(() =>
      expect(screen.getByDisplayValue('Annual')).toBeInTheDocument(),
    );
    expect(
      screen.queryByRole('button', { name: 'Save' }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Deactivate' }),
    ).not.toBeInTheDocument();
  });
});
