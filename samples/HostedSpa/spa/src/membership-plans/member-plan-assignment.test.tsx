import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AppShellProvider } from '@bifrostql/app-shell';
import type { AppMetadata } from '@bifrostql/app-shell';
import { MemberPlanAssignment } from './member-plan-assignment';

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
 * `main.member_memberships` link entity with `member_id` / `plan_id`
 * `fk-lookup` fields, a `start_date` datepicker, and a `status` select.
 */
const sampleMetadata: AppMetadata = {
  entities: {
    'main.member_memberships': {
      label: 'Member Memberships',
      displayFields: ['status'],
      fields: {
        member_id: { widget: 'fk-lookup', group: 'Enrollment' },
        plan_id: { widget: 'fk-lookup', group: 'Enrollment' },
        start_date: { widget: 'datepicker', group: 'Term' },
        end_date: { widget: 'datepicker', group: 'Term' },
        status: { widget: 'select', group: 'Term' },
        tenant_id: {
          widget: 'fk-lookup',
          group: 'Admin',
          visible: false,
          readOnly: true,
        },
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
    'main.membership_plans': {
      label: 'Membership Plans',
      displayFields: ['name'],
      fields: { name: { widget: 'text' }, is_active: { widget: 'checkbox' } },
    },
  },
};

/** Records the GraphQL request bodies the component issues. */
let graphqlRequests: Array<{ query: string; variables: unknown }>;

const membershipRows = [
  {
    id: 21,
    member_id: 7,
    plan_id: 3,
    start_date: '2025-01-01',
    end_date: null,
    status: 'active',
  },
];

const memberRows = [
  { id: 7, first_name: 'Ada', last_name: 'Lovelace' },
  { id: 8, first_name: 'Grace', last_name: 'Hopper' },
];

const planRows = [
  { id: 3, name: 'Annual', is_active: true },
  { id: 4, name: 'Monthly', is_active: true },
];

function createFetchMock(
  identity: TestIdentity | null,
  metadata: AppMetadata = sampleMetadata,
  memberships: Array<Record<string, unknown>> = membershipRows,
  members: Array<Record<string, unknown>> = memberRows,
  plans: Array<Record<string, unknown>> = planRows,
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
            Promise.resolve({ data: { main_member_memberships: 1 } }),
        } as Response);
      }
      if (/main_members\b/.test(body.query)) {
        return Promise.resolve({
          ok: true,
          status: 200,
          statusText: 'OK',
          json: () => Promise.resolve({ data: { main_members: members } }),
        } as Response);
      }
      if (/main_membership_plans\b/.test(body.query)) {
        return Promise.resolve({
          ok: true,
          status: 200,
          statusText: 'OK',
          json: () =>
            Promise.resolve({ data: { main_membership_plans: plans } }),
        } as Response);
      }
    }
    // The member_memberships list.
    return Promise.resolve({
      ok: true,
      status: 200,
      statusText: 'OK',
      json: () =>
        Promise.resolve({ data: { main_member_memberships: memberships } }),
    } as Response);
  });
}

function renderScreen() {
  return render(
    <AppShellProvider config={{ endpoint: ENDPOINT }}>
      <MemberPlanAssignment />
    </AppShellProvider>,
  );
}

describe('MemberPlanAssignment', () => {
  let originalFetch: typeof globalThis.fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it('lists existing assignments resolving member and plan to display names', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.write']));

    // Act
    renderScreen();

    // Assert: the row shows names, not raw FK ids.
    await waitFor(() =>
      expect(
        screen.getByTestId('member-plan-assignment'),
      ).toBeInTheDocument(),
    );
    const list = screen.getByTestId('member-plan-assignment-list');
    expect(list).toHaveTextContent('Ada Lovelace');
    expect(list).toHaveTextContent('Annual');
    expect(list).toHaveTextContent('active');
  });

  it('assigns a member to a plan via fk-lookup selects — no manual FK id entry', async () => {
    // Arrange
    const user = userEvent.setup();
    globalThis.fetch = createFetchMock(identityWith(['main.members.write']));
    renderScreen();
    await waitFor(() =>
      expect(
        screen.getByTestId('member-plan-assignment-add'),
      ).toBeInTheDocument(),
    );

    // Act: pick a member and a plan from fk-lookup <select>s, set the renewal
    // date and status, then assign.
    const memberSelect = screen.getByLabelText('member_id');
    expect(memberSelect.tagName).toBe('SELECT');
    expect(memberSelect).toHaveTextContent('Grace Hopper');
    await user.selectOptions(memberSelect, '8');

    const planSelect = screen.getByLabelText('plan_id');
    expect(planSelect.tagName).toBe('SELECT');
    expect(planSelect).toHaveTextContent('Monthly');
    await user.selectOptions(planSelect, '4');

    await user.clear(screen.getByLabelText('start_date'));
    await user.type(screen.getByLabelText('start_date'), '2025-06-01');
    await user.selectOptions(screen.getByLabelText('status'), 'active');
    await user.click(screen.getByRole('button', { name: 'Assign plan' }));

    // Assert: an insert mutation carrying selected member_id + plan_id +
    // renewal date + status.
    await waitFor(() => {
      const mutation = graphqlRequests.find((r) =>
        /\binsert\b/.test(r.query),
      );
      expect(mutation).toBeDefined();
      const detail = (mutation!.variables as { detail: Record<string, unknown> })
        .detail;
      expect(detail.member_id).toBe('8');
      expect(detail.plan_id).toBe('4');
      expect(detail.start_date).toBe('2025-06-01');
      expect(detail.status).toBe('active');
    });
  });

  it('does not assign when no member or plan is selected', async () => {
    // Arrange
    const user = userEvent.setup();
    globalThis.fetch = createFetchMock(identityWith(['main.members.write']));
    renderScreen();
    await waitFor(() =>
      expect(
        screen.getByTestId('member-plan-assignment-add'),
      ).toBeInTheDocument(),
    );

    // Act: click Assign without picking a member/plan.
    await user.click(screen.getByRole('button', { name: 'Assign plan' }));

    // Assert: no insert mutation issued.
    expect(
      graphqlRequests.find((r) => /\binsert\b/.test(r.query)),
    ).toBeUndefined();
  });

  it('hides assigning controls for a read-only session', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']));

    // Act
    renderScreen();

    // Assert: the assignment list still shows, but no add controls.
    await waitFor(() =>
      expect(
        screen.getByTestId('member-plan-assignment-list'),
      ).toHaveTextContent('Ada Lovelace'),
    );
    expect(
      screen.queryByRole('button', { name: 'Assign plan' }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByTestId('member-plan-assignment-add'),
    ).not.toBeInTheDocument();
  });
});
