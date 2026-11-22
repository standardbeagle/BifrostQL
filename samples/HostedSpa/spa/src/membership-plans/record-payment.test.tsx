import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AppShellProvider } from '@bifrostql/app-shell';
import type { AppMetadata } from '@bifrostql/app-shell';
import { RecordPayment } from './record-payment';
import { MEMBERS_FINANCE } from './finance-fields';

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

/** Records the record-payment workflow request bodies the component issues. */
let workflowRequests: Array<Record<string, unknown>>;

const RECORD_PAYMENT_ENDPOINT = '/workflows/membership/record-payment';

/** The row window the server applies to a query that names no `limit`. */
const SERVER_DEFAULT_ROW_WINDOW = 100;

type Row = Record<string, unknown>;

/**
 * Two open invoices plus one already-PAID invoice (53) that a recorded
 * payment points at — a paid invoice is absent from the open-invoice select
 * but must still label its payment row.
 */
function invoiceFixture(): Row[] {
  return [
    { id: 51, member_id: 7, member_membership_id: 21, amount_cents: 12000, status: 'open' },
    { id: 52, member_id: 8, member_membership_id: 22, amount_cents: 6000, status: 'open' },
    { id: 53, member_id: 9, member_membership_id: 23, amount_cents: 3000, status: 'paid' },
  ];
}

function paymentFixture(): Row[] {
  return [
    { id: 91, invoice_id: 51, amount_cents: 5000, paid_on: '2025-02-01', method: 'card' },
    { id: 92, invoice_id: 53, amount_cents: 3000, paid_on: '2025-01-15', method: 'cash' },
  ];
}

const memberRows: Row[] = [
  { id: 7, first_name: 'Ada', last_name: 'Lovelace' },
  { id: 8, first_name: 'Grace', last_name: 'Hopper' },
  { id: 9, first_name: 'Katherine', last_name: 'Johnson' },
];

function membershipFixture(): Row[] {
  return [
    { id: 21, member_id: 7, plan_id: 3, status: 'lapsed' },
    { id: 22, member_id: 8, plan_id: 4, status: 'lapsed' },
    { id: 23, member_id: 9, plan_id: 4, status: 'active' },
  ];
}

interface MockOptions {
  invoices?: Row[];
  payments?: Row[];
  /** HTTP status the workflow endpoint answers with; 200 applies the write. */
  workflowStatus?: number;
  /** Body text of a non-2xx workflow answer. */
  workflowBody?: string;
  /** When set, the workflow answer waits until this promise resolves. */
  workflowGate?: Promise<void>;
}

function jsonResponse(payload: unknown, status = 200): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    statusText: String(status),
    json: () => Promise.resolve(payload),
    text: () =>
      Promise.resolve(typeof payload === 'string' ? payload : JSON.stringify(payload)),
  } as Response;
}

/** The condition on `field` in a read's `$filter` variable, looked for through `and`. */
function conditionOn(filter: unknown, field: string): Record<string, unknown> | undefined {
  if (!filter || typeof filter !== 'object') return undefined;
  const f = filter as Record<string, unknown>;
  if (f[field] && typeof f[field] === 'object') return f[field] as Record<string, unknown>;
  for (const part of (f.and as unknown[] | undefined) ?? []) {
    const found = conditionOn(part, field);
    if (found) return found;
  }
  return undefined;
}

/** Applies the `{ _eq }` / `{ _in }` condition on `field` that the read sends in `variables.filter`. */
function applyFilter(variables: unknown, rows: Row[], field: string): Row[] {
  const condition = conditionOn((variables as { filter?: unknown } | null)?.filter, field);
  if (condition && '_eq' in condition) {
    return rows.filter((r) => String(r[field]) === String(condition._eq));
  }
  if (condition && Array.isArray(condition._in)) {
    const values = condition._in.map(String);
    return rows.filter((r) => values.includes(String(r[field])));
  }
  return rows;
}

/**
 * A fetch mock that behaves like the host: row queries honour their `$filter`
 * variable and `limit` (default 100) and report the unpaged `total`; the
 * record-payment workflow, when it succeeds, inserts the payment, marks the
 * invoice paid and advances the membership — all server-side.
 */
function createFetchMock(
  identity: TestIdentity | null,
  metadata: AppMetadata = sampleMetadata,
  options: MockOptions = {},
) {
  graphqlRequests = [];
  workflowRequests = [];
  const invoices = options.invoices ?? invoiceFixture();
  const payments = options.payments ?? paymentFixture();
  const memberships = membershipFixture();

  return vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === 'string' ? input : input.toString();

    if (url.includes('/auth/session')) {
      return identity === null
        ? jsonResponse(null, 401)
        : jsonResponse(identity);
    }

    if (url.includes('/_app-metadata')) {
      return jsonResponse(metadata);
    }

    if (url.includes(RECORD_PAYMENT_ENDPOINT)) {
      const request = JSON.parse(init!.body as string) as Row;
      workflowRequests.push(request);
      if (options.workflowGate) {
        await options.workflowGate;
      }
      const status = options.workflowStatus ?? 200;
      if (status !== 200) {
        return jsonResponse(options.workflowBody ?? '', status);
      }
      const invoice = invoices.find((i) => String(i.id) === String(request.invoiceId));
      payments.unshift({
        id: 100 + payments.length,
        invoice_id: request.invoiceId,
        amount_cents: request.amountCents,
        paid_on: request.paidOn,
        method: request.method,
      });
      if (invoice) {
        invoice.status = 'paid';
        const membership = memberships.find(
          (m) => String(m.id) === String(invoice.member_membership_id),
        );
        if (membership) {
          membership.status = 'active';
        }
      }
      return jsonResponse(null);
    }

    const body = init?.body
      ? (JSON.parse(init.body as string) as { query: string; variables: unknown })
      : null;
    if (!body) {
      return jsonResponse({ data: {} });
    }
    graphqlRequests.push(body);
    const limitMatch = /\blimit:\s*(\d+)/.exec(body.query);
    const limit = limitMatch ? Number(limitMatch[1]) : SERVER_DEFAULT_ROW_WINDOW;
    const window = (rows: Row[]) => ({ total: rows.length, data: rows.slice(0, limit) });

    if (/main_dues_invoices\b/.test(body.query)) {
      const rows = applyFilter(body.variables, applyFilter(body.variables, invoices, 'status'), 'id');
      return jsonResponse({ data: { main_dues_invoices: window(rows) } });
    }
    if (/main_dues_payments\b/.test(body.query)) {
      return jsonResponse({ data: { main_dues_payments: window(payments) } });
    }
    if (/main_members\b/.test(body.query)) {
      return jsonResponse({
        data: { main_members: window(applyFilter(body.variables, memberRows, 'id')) },
      });
    }
    if (/main_member_memberships\b/.test(body.query)) {
      return jsonResponse({ data: { main_member_memberships: window(memberships) } });
    }
    return jsonResponse({ data: {} });
  });
}

function renderScreen() {
  return render(
    <AppShellProvider config={{ endpoint: ENDPOINT }}>
      <RecordPayment />
    </AppShellProvider>,
  );
}

const FINANCE_WRITER = ['main.members.write', MEMBERS_FINANCE];

async function fillPayment(
  user: ReturnType<typeof userEvent.setup>,
  amount = '6000',
) {
  await user.selectOptions(screen.getByLabelText('invoice_id'), '52');
  await user.clear(screen.getByLabelText('amount_cents'));
  await user.type(screen.getByLabelText('amount_cents'), amount);
  await user.clear(screen.getByLabelText('paid_on'));
  await user.type(screen.getByLabelText('paid_on'), '2025-06-01');
  await user.selectOptions(screen.getByLabelText('method'), 'check');
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
    // Arrange: a finance session sees the amount column.
    globalThis.fetch = createFetchMock(identityWith(FINANCE_WRITER));

    // Act
    renderScreen();

    // Assert: the row for the open invoice shows its member, not a raw FK id.
    await waitFor(() =>
      expect(screen.getByTestId('record-payment-91')).toHaveTextContent(
        'Ada Lovelace',
      ),
    );
    expect(screen.getByTestId('record-payment-91')).toHaveTextContent('5000');
  });

  it('labels a payment against an already-paid invoice by member name', async () => {
    // Arrange: payment 92 points at invoice 53, which is `paid` — absent from
    // the open-invoice select.
    globalThis.fetch = createFetchMock(identityWith(FINANCE_WRITER));

    // Act
    renderScreen();

    // Assert: the member name, never the raw invoice id.
    await waitFor(() =>
      expect(screen.getByTestId('record-payment-92')).toHaveTextContent(
        'Katherine Johnson',
      ),
    );
  });

  it('records a payment through the record-payment workflow endpoint', async () => {
    // Arrange
    const user = userEvent.setup();
    globalThis.fetch = createFetchMock(identityWith(FINANCE_WRITER));
    renderScreen();
    await waitFor(() =>
      expect(screen.getByTestId('record-payment-add')).toBeInTheDocument(),
    );
    // Invoices are labelled by member name + amount, not raw ids.
    const invoiceSelect = screen.getByLabelText('invoice_id');
    expect(invoiceSelect.tagName).toBe('SELECT');
    expect(invoiceSelect).toHaveTextContent('Grace Hopper');

    // Act
    await fillPayment(user);
    await user.click(screen.getByRole('button', { name: 'Record payment' }));

    // Assert: one workflow request carrying the selected invoice, amount,
    // date and method...
    await waitFor(() => expect(workflowRequests).toHaveLength(1));
    expect(workflowRequests[0]).toEqual({
      invoiceId: 52,
      amountCents: 6000,
      paidOn: '2025-06-01',
      method: 'check',
    });
    // ...and no client-side GraphQL write: the workflow owns every table.
    expect(
      graphqlRequests.find((r) => /\bmutation\b/.test(r.query)),
    ).toBeUndefined();
  });

  it('shows the membership the workflow advanced and drops the paid invoice', async () => {
    // Arrange
    const user = userEvent.setup();
    globalThis.fetch = createFetchMock(identityWith(FINANCE_WRITER));
    renderScreen();
    await waitFor(() =>
      expect(screen.getByTestId('record-payment-add')).toBeInTheDocument(),
    );

    // Act: record against invoice 52 (membership 22, lapsed).
    await fillPayment(user);
    await user.click(screen.getByRole('button', { name: 'Record payment' }));

    // Assert: the status read back off the server is `active`...
    await waitFor(() =>
      expect(screen.getByTestId('record-payment-status')).toHaveTextContent(
        'active',
      ),
    );
    // ...and the now-paid invoice can no longer be picked for a payment.
    await waitFor(() =>
      expect(
        screen.getByLabelText('invoice_id').querySelector('option[value="52"]'),
      ).toBeNull(),
    );
  });

  it('disables the Record button while the workflow request is in flight', async () => {
    // Arrange: the workflow answer is held until the test releases it.
    let release!: () => void;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    const user = userEvent.setup();
    globalThis.fetch = createFetchMock(identityWith(FINANCE_WRITER), sampleMetadata, {
      workflowGate: gate,
    });
    renderScreen();
    await waitFor(() =>
      expect(screen.getByTestId('record-payment-add')).toBeInTheDocument(),
    );
    await fillPayment(user);
    const button = screen.getByRole('button', { name: 'Record payment' });

    // Act: double-click while the first request is still pending.
    await user.dblClick(button);

    // Assert: one request, and the button is disabled until it answers.
    await waitFor(() => expect(workflowRequests).toHaveLength(1));
    expect(button).toBeDisabled();
    release();
    await waitFor(() =>
      expect(screen.getByTestId('record-payment-write-notice')).toBeInTheDocument(),
    );
    expect(workflowRequests).toHaveLength(1);
  });

  it('keeps the form and claims no status when the workflow refuses', async () => {
    // Arrange: the endpoint's policy pre-flight refuses the caller.
    const user = userEvent.setup();
    globalThis.fetch = createFetchMock(identityWith(FINANCE_WRITER), sampleMetadata, {
      workflowStatus: 403,
    });
    renderScreen();
    await waitFor(() =>
      expect(screen.getByTestId('record-payment-add')).toBeInTheDocument(),
    );

    // Act
    await fillPayment(user);
    await user.click(screen.getByRole('button', { name: 'Record payment' }));

    // Assert: the refusal is reported, no status is claimed, and the entered
    // values survive for a retry.
    await waitFor(() =>
      expect(screen.getByTestId('record-payment-write-error')).toHaveTextContent(
        'not permitted',
      ),
    );
    expect(screen.queryByTestId('record-payment-status')).not.toBeInTheDocument();
    expect(screen.getByLabelText('amount_cents')).toHaveValue('6000');
    expect(screen.getByLabelText('paid_on')).toHaveValue('2025-06-01');
    expect(screen.getByLabelText('invoice_id')).toHaveValue('52');
  });

  it('refuses a non-positive amount without calling the workflow', async () => {
    // Arrange
    const user = userEvent.setup();
    globalThis.fetch = createFetchMock(identityWith(FINANCE_WRITER));
    renderScreen();
    await waitFor(() =>
      expect(screen.getByTestId('record-payment-add')).toBeInTheDocument(),
    );

    // Act
    await fillPayment(user, '0');
    await user.click(screen.getByRole('button', { name: 'Record payment' }));

    // Assert
    await waitFor(() =>
      expect(screen.getByTestId('record-payment-write-error')).toHaveTextContent(
        'greater than 0',
      ),
    );
    expect(workflowRequests).toHaveLength(0);
  });

  it('does not record when no invoice is selected', async () => {
    // Arrange
    const user = userEvent.setup();
    globalThis.fetch = createFetchMock(identityWith(FINANCE_WRITER));
    renderScreen();
    await waitFor(() =>
      expect(screen.getByTestId('record-payment-add')).toBeInTheDocument(),
    );

    // Act: click Record without picking an invoice.
    await user.click(screen.getByRole('button', { name: 'Record payment' }));

    // Assert: nothing was sent.
    expect(workflowRequests).toHaveLength(0);
    expect(
      graphqlRequests.find((r) => /\bmutation\b/.test(r.query)),
    ).toBeUndefined();
  });

  it('says so when more open invoices or payments exist than the list loads', async () => {
    // Arrange: 600 open invoices and 600 payments — past the screen's
    // 500-row list window (and the server's 100-row default).
    const invoices: Row[] = Array.from({ length: 600 }, (_, i) => ({
      id: 1000 + i,
      member_id: 7,
      member_membership_id: 21,
      amount_cents: 100,
      status: 'open',
    }));
    const payments: Row[] = Array.from({ length: 600 }, (_, i) => ({
      id: 5000 + i,
      invoice_id: 1000 + i,
      amount_cents: 100,
      paid_on: '2025-01-01',
      method: 'card',
    }));
    globalThis.fetch = createFetchMock(identityWith(FINANCE_WRITER), sampleMetadata, {
      invoices,
      payments,
    });

    // Act
    renderScreen();

    // Assert: both lists name what they show against the real total.
    await waitFor(() =>
      expect(
        screen.getByTestId('record-payment-invoices-truncated'),
      ).toHaveTextContent('Showing the first 500 of 600 open invoices'),
    );
    expect(
      screen.getByTestId('record-payment-payments-truncated'),
    ).toHaveTextContent('Showing the 500 most recent of 600 payments');
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
    expect(screen.queryByTestId('record-payment-add')).not.toBeInTheDocument();
  });

  it('never queries amount_cents and offers no recording to a non-finance writer', async () => {
    // Arrange: a writer WITHOUT main.members.finance. The workflow needs an
    // amount the session may neither read nor write.
    globalThis.fetch = createFetchMock(identityWith(['main.members.write']));

    // Act
    renderScreen();
    await waitFor(() =>
      expect(
        screen.getByTestId('record-payment-finance-required'),
      ).toBeInTheDocument(),
    );

    // Assert: no recording form, no amount cell, and no GraphQL query names
    // the policy-read-denied column.
    expect(screen.queryByTestId('record-payment-add')).not.toBeInTheDocument();
    expect(screen.queryByTestId('record-payment-91-amount')).not.toBeInTheDocument();
    expect(
      graphqlRequests.find((r) => /amount_cents/.test(r.query)),
    ).toBeUndefined();
  });

  it('shows amount_cents and queries it for a finance writer', async () => {
    // Arrange: a writer WITH main.members.finance.
    globalThis.fetch = createFetchMock(identityWith(FINANCE_WRITER));

    // Act
    renderScreen();
    await waitFor(() =>
      expect(screen.getByTestId('record-payment-add')).toBeInTheDocument(),
    );

    // Assert: the amount control is present and the recorded amount renders.
    expect(screen.getByLabelText('amount_cents')).toBeInTheDocument();
    expect(screen.getByTestId('record-payment-91-amount')).toHaveTextContent('5000');
    expect(
      graphqlRequests.find((r) => /amount_cents/.test(r.query)),
    ).toBeDefined();
  });

  it('reports a missing dues_payments overlay entity', async () => {
    // Arrange: an overlay without the dues_payments entity.
    const incomplete: AppMetadata = {
      entities: {
        'main.members': sampleMetadata.entities!['main.members'],
      },
    };
    globalThis.fetch = createFetchMock(identityWith(['main.members.write']), incomplete);

    // Act
    renderScreen();

    // Assert
    await waitFor(() =>
      expect(screen.getByTestId('record-payment-missing')).toBeInTheDocument(),
    );
  });
});
