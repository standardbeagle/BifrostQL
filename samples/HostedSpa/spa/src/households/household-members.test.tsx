import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AppShellProvider } from '@bifrostql/app-shell';
import type { AppMetadata } from '@bifrostql/app-shell';
import { HouseholdMembers } from './household-members';

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
 * `main.household_members` link entity with `household_id` / `member_id`
 * `fk-lookup` fields and a `relationship` select.
 */
const sampleMetadata: AppMetadata = {
  entities: {
    'main.household_members': {
      label: 'Household Members',
      displayFields: ['relationship'],
      fields: {
        household_id: { widget: 'fk-lookup', group: 'Link' },
        member_id: { widget: 'fk-lookup', group: 'Link' },
        relationship: { widget: 'select', group: 'Link' },
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
  },
};

/** Records the GraphQL request bodies the component issues. */
let graphqlRequests: Array<{ query: string; variables: unknown }>;

const linkRows = [
  { id: 11, household_id: 4, member_id: 7, relationship: 'head' },
  { id: 12, household_id: 4, member_id: 8, relationship: 'spouse' },
];

const memberRows = [
  { id: 7, first_name: 'Ada', last_name: 'Lovelace' },
  { id: 8, first_name: 'Grace', last_name: 'Hopper' },
  { id: 9, first_name: 'Alan', last_name: 'Turing' },
];

function createFetchMock(
  identity: TestIdentity | null,
  metadata: AppMetadata = sampleMetadata,
  links: Array<Record<string, unknown>> = linkRows,
  members: Array<Record<string, unknown>> = memberRows,
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
            Promise.resolve({ data: { main_household_members: 1 } }),
        } as Response);
      }
      if (/main_members/.test(body.query)) {
        return Promise.resolve({
          ok: true,
          status: 200,
          statusText: 'OK',
          json: () => Promise.resolve({ data: { main_members: members } }),
        } as Response);
      }
    }
    // The household_members link list.
    return Promise.resolve({
      ok: true,
      status: 200,
      statusText: 'OK',
      json: () =>
        Promise.resolve({ data: { main_household_members: links } }),
    } as Response);
  });
}

function renderControls(householdId: number) {
  return render(
    <AppShellProvider config={{ endpoint: ENDPOINT }}>
      <HouseholdMembers householdId={householdId} />
    </AppShellProvider>,
  );
}

describe('HouseholdMembers', () => {
  let originalFetch: typeof globalThis.fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it('lists the existing relationship links for the household', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.write']));

    // Act
    renderControls(4);

    // Assert: each link row resolves the member to a display name, not a raw id.
    await waitFor(() =>
      expect(screen.getByTestId('household-members')).toBeInTheDocument(),
    );
    const list = screen.getByTestId('household-members-list');
    expect(list).toHaveTextContent('Ada Lovelace');
    expect(list).toHaveTextContent('Grace Hopper');
    expect(list).toHaveTextContent('head');
    expect(list).toHaveTextContent('spouse');
  });

  it('adds a member link via the fk-lookup select — no manual FK id entry', async () => {
    // Arrange
    const user = userEvent.setup();
    globalThis.fetch = createFetchMock(identityWith(['main.members.write']));
    renderControls(4);
    await waitFor(() =>
      expect(screen.getByTestId('household-members')).toBeInTheDocument(),
    );

    // Act: pick a member from the fk-lookup <select>, then add.
    const memberSelect = screen.getByLabelText('member_id');
    expect(memberSelect.tagName).toBe('SELECT');
    // The select offers candidate members by name — proves no manual id entry.
    expect(memberSelect).toHaveTextContent('Alan Turing');
    await user.selectOptions(memberSelect, '9');
    await user.selectOptions(screen.getByLabelText('relationship'), 'child');
    await user.click(screen.getByRole('button', { name: 'Add member' }));

    // Assert: an insert mutation carrying household_id + selected member_id.
    await waitFor(() => {
      const mutation = graphqlRequests.find((r) =>
        /\binsert\b/.test(r.query),
      );
      expect(mutation).toBeDefined();
      const detail = (mutation!.variables as { detail: Record<string, unknown> })
        .detail;
      expect(detail.household_id).toBe(4);
      expect(detail.member_id).toBe('9');
      expect(detail.relationship).toBe('child');
    });
  });

  it('edits the relationship of an existing link via an update mutation', async () => {
    // Arrange
    const user = userEvent.setup();
    globalThis.fetch = createFetchMock(identityWith(['main.members.write']));
    renderControls(4);
    await waitFor(() =>
      expect(screen.getByTestId('household-members')).toBeInTheDocument(),
    );

    // Act: change the relationship select on link row 11.
    await user.selectOptions(
      screen.getByTestId('household-member-relationship-11'),
      'child',
    );
    await user.click(screen.getByTestId('household-member-save-11'));

    // Assert
    await waitFor(() => {
      const mutation = graphqlRequests.find((r) =>
        /\bupdate\b/.test(r.query),
      );
      expect(mutation).toBeDefined();
      const detail = (mutation!.variables as { detail: Record<string, unknown> })
        .detail;
      expect(detail.id).toBe(11);
      expect(detail.relationship).toBe('child');
    });
  });

  it('removes a member link via a delete mutation', async () => {
    // Arrange
    const user = userEvent.setup();
    globalThis.fetch = createFetchMock(identityWith(['main.members.write']));
    renderControls(4);
    await waitFor(() =>
      expect(screen.getByTestId('household-members')).toBeInTheDocument(),
    );

    // Act
    await user.click(screen.getByTestId('household-member-remove-12'));

    // Assert
    await waitFor(() => {
      const mutation = graphqlRequests.find((r) =>
        /\bdelete\b/.test(r.query),
      );
      expect(mutation).toBeDefined();
      const detail = (mutation!.variables as { detail: Record<string, unknown> })
        .detail;
      expect(detail.id).toBe(12);
    });
  });

  it('hides add / edit / remove controls for a read-only session', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']));

    // Act
    renderControls(4);

    // Assert: the links are still listed, but no mutating controls are offered.
    await waitFor(() =>
      expect(screen.getByTestId('household-members-list')).toHaveTextContent(
        'Ada Lovelace',
      ),
    );
    expect(
      screen.queryByRole('button', { name: 'Add member' }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByTestId('household-member-remove-12'),
    ).not.toBeInTheDocument();
  });
});
