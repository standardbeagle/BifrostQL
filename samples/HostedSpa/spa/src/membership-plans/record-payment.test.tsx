import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AppShellProvider } from '@bifrostql/app-shell';
import type { AppMetadata } from '@bifrostql/app-shell';
import { RecordPayment } from './record-payment';

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
 * `main.dues_payments` entity with an `invoice_id` `fk-lookup`, an
 * `amount_cents` number, a `paid_on` datepicker, and a `method` select.
 */
const sampleMetadata: AppMetadata = {
  entities: {
    'main.dues_payments': {
      label: 'Dues Payments',
      displayFields: ['amount_cents'],
      fields: {
        invoice_id: { widget: 'fk-lookup', group: 'Payment' },
        amount_cents: { widget: 'number', group: 'Payment' },
        paid_on: { widget: 'datepicker', group: 'Payment' },
        method: { widget: 'select', group: 'Payment' },
        tenant_id: {
          widget: 'fk-lookup',
          group: 'Admin',
          visible: false,
          readOnly: true,
        },
      },
    },
    'main.dues_invoices': {
      label: 'Dues Invoices',
      displayFields: ['amount_cents'],
      fields: {
        member_id: { widget: 'fk-lookup' },
        member_membership_id: { widget: 'fk-lookup' },
        amount_cents: { widget: 'number' },
        status: { widget: 'select' },
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
    'main.member_memberships': {
      label: 'Member Memberships',
      displayFields: ['status'],
      fields: {
        member_id: { widget: 'fk-lookup' },
        plan_id: { widget: 'fk-lookup' },
        status: { widget: 'select' },
      },
    },
  },
};

/** Records the GraphQL request bodies the component issues. */
let graphqlRequests: Array<{ query: string; variables: unknown }>;

const invoiceRows = [
  {
    id: 51,
    member_id: 7,
    member_membership_id: 21,
    amount_cents: 12000,
    status: 'open',
  },
  {
    id: 52,
    member_id: 8,
    member_membership_id: 22,
    amount_cents: 6000,
    status: 'open',
  },
];

const paymentRows = [
  { id: 91, invoice_id: 51, amount_cents: 5000, paid_on: '2025-02-01', method: 'card' },
];

const memberRows = [
  { id: 7, first_name: 'Ada', last_name: 'Lovelace' },
  { id: 8, first_name: 'Grace', last_name: 'Hopper' },
];

const membershipRows = [
  { id: 21, member_id: 7, plan_id: 3, status: 'lapsed' },
  { id: 22, member_id: 8, plan_id: 4, status: 'lapsed' },
];

function createFetchMock(
  identity: TestIdentity | null,
  metadata: AppMetadata = sampleMetadata,
  invoices: Array<Record<string, unknown>> = invoiceRows,
  payments: Array<Record<string, unknown>> = paymentRows,
  members: Array<Record<string, unknown>> = memberRows,
  memberships: Array<Record<string, unknown>> = membershipRows,
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
          json: () => Promise.resolve({ data: { mutation: 1 } }),
        } as Response);
      }
      if (/main_dues_invoices\b/.test(body.query)) {
        return Promise.resolve({
          ok: true,
          status: 200,
          statusText: 'OK',
          json: () =>
            Promise.resolve({ data: { main_dues_invoices: invoices } }),
        } as Response);
      }
      if (/main_dues_payments\b/.test(body.query)) {
        return Promise.resolve({
          ok: true,
          status: 200,
          statusText: 'OK',
          json: () =>
            Promise.resolve({ data: { main_dues_payments: payments } }),
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
      if (/main_member_memberships\b/.test(body.query)) {
        return Promise.resolve({
          ok: true,
          status: 200,
          statusText: 'OK',
          json: () =>
            Promise.resolve({ data: { main_member_memberships: memberships } }),
        } as Response);
      }
    }
    return Promise.resolve({
      ok: true,
      status: 200,
      statusText: 'OK',
      json: () => Promise.resolve({ data: {} }),
    } as Response);
  });
}

function renderScreen() {
  return render(
    <AppShellProvider config={{ endpoint: ENDPOINT }}>
      <RecordPayment />
    </AppShellProvider>,
  );
}

describe('RecordPayment', () => {
  let originalFetch: typeof globalThis.fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it('lists recorded payments resolving each invoice to a member name', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.write']));

    // Act
    renderScreen();

    // Assert: the recorded-payment row shows the member name, not a raw FK id.
    await waitFor(() =>
      expect(screen.getByTestId('record-payment')).toBeInTheDocument(),
    );
    const list = screen.getByTestId('record-payment-list');
    expect(list).toHaveTextContent('Ada Lovelace');
    expect(list).toHaveTextContent('5000');
  });

  it('records a payment via fk-lookup invoice select — no manual FK id entry', async () => {
    // Arrange
    const user = userEvent.setup();
    globalThis.fetch = createFetchMock(identityWith(['main.members.write']));
    renderScreen();
    await waitFor(() =>
      expect(screen.getByTestId('record-payment-add')).toBeInTheDocument(),
    );

    // Act: pick an open invoice from the fk-lookup <select>, enter amount,
    // date, and method, then record.
    const invoiceSelect = screen.getByLabelText('invoice_id');
    expect(invoiceSelect.tagName).toBe('SELECT');
    // Invoices are labelled by member name + amount, not raw ids.
    expect(invoiceSelect).toHaveTextContent('Grace Hopper');
    await user.selectOptions(invoiceSelect, '52');

    await user.clear(screen.getByLabelText('amount_cents'));
    await user.type(screen.getByLabelText('amount_cents'), '6000');
    await user.clear(screen.getByLabelText('paid_on'));
    await user.type(screen.getByLabelText('paid_on'), '2025-06-01');
    await user.selectOptions(screen.getByLabelText('method'), 'check');
    await user.click(screen.getByRole('button', { name: 'Record payment' }));

    // Assert: an insert mutation on dues_payments carrying the selected
    // invoice, amount, date, and method.
    await waitFor(() => {
      const insert = graphqlRequests.find((r) => /\binsert\b/.test(r.query));
      expect(insert).toBeDefined();
      const detail = (insert!.variables as { detail: Record<string, unknown> })
        .detail;
      expect(detail.invoice_id).toBe('52');
      expect(detail.amount_cents).toBe(6000);
      expect(detail.paid_on).toBe('2025-06-01');
      expect(detail.method).toBe('check');
    });
  });

  it('advances the member membership status to active after a payment', async () => {
    // Arrange
    const user = userEvent.setup();
    globalThis.fetch = createFetchMock(identityWith(['main.members.write']));
    renderScreen();
    await waitFor(() =>
      expect(screen.getByTestId('record-payment-add')).toBeInTheDocument(),
    );

    // Act: record a payment against invoice 52 (membership 22, lapsed).
    await user.selectOptions(screen.getByLabelText('invoice_id'), '52');
    await user.clear(screen.getByLabelText('amount_cents'));
    await user.type(screen.getByLabelText('amount_cents'), '6000');
    await user.click(screen.getByRole('button', { name: 'Record payment' }));

    // Assert: an update mutation on member_memberships flips status to active,
    // and the screen surfaces the updated status.
    await waitFor(() => {
      const update = graphqlRequests.find(
        (r) =>
          /\bupdate\b/.test(r.query) &&
          /member_memberships/.test(r.query),
      );
      expect(update).toBeDefined();
      const detail = (update!.variables as { detail: Record<string, unknown> })
        .detail;
      expect(detail.id).toBe(22);
      expect(detail.status).toBe('active');
    });
    await waitFor(() =>
      expect(screen.getByTestId('record-payment-status')).toHaveTextContent(
        'active',
      ),
    );
  });

  it('does not record when no invoice is selected', async () => {
    // Arrange
    const user = userEvent.setup();
    globalThis.fetch = createFetchMock(identityWith(['main.members.write']));
    renderScreen();
    await waitFor(() =>
      expect(screen.getByTestId('record-payment-add')).toBeInTheDocument(),
    );

    // Act: click Record without picking an invoice.
    await user.click(screen.getByRole('button', { name: 'Record payment' }));

    // Assert: no mutation issued.
    expect(
      graphqlRequests.find((r) => /\bmutation\b/.test(r.query)),
    ).toBeUndefined();
  });

  it('hides recording controls for a read-only session', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']));

    // Act
    renderScreen();

    // Assert: the payment list still shows, but no recording controls.
    await waitFor(() =>
      expect(screen.getByTestId('record-payment-list')).toHaveTextContent(
        'Ada Lovelace',
      ),
    );
    expect(
      screen.queryByRole('button', { name: 'Record payment' }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByTestId('record-payment-add'),
    ).not.toBeInTheDocument();
  });

  it('reports a missing dues_payments overlay entity', async () => {
    // Arrange: an overlay without the dues_payments entity.
    const incomplete: AppMetadata = {
      entities: {
        'main.members': sampleMetadata.entities!['main.members'],
      },
    };
    globalThis.fetch = createFetchMock(
      identityWith(['main.members.write']),
      incomplete,
    );

    // Act
    renderScreen();

    // Assert
    await waitFor(() =>
      expect(
        screen.getByTestId('record-payment-missing'),
      ).toBeInTheDocument(),
    );
  });
});
