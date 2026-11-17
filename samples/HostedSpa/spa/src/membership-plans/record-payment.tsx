import { useMemo, useState } from 'react';
import {
  FieldControl,
  entityKeyToQueryName,
  useAppMetadata,
  useSession,
} from '@bifrostql/app-shell';
import { useBifrostQuery } from '@bifrostql/react';
import { canReadFinanceFields } from './finance-fields';
import { useWriteFeedback, WriteFeedbackRegion } from '../common/write-feedback';

/** Qualified entity key of the dues_payments entity in the overlay. */
const DUES_PAYMENTS_ENTITY_KEY = 'main.dues_payments';

/** Qualified entity key of the dues_invoices entity, an fk-lookup target. */
const DUES_INVOICES_ENTITY_KEY = 'main.dues_invoices';

/** Qualified entity key of the members entity, used to label invoices. */
const MEMBERS_ENTITY_KEY = 'main.members';

/** Qualified entity key of the member_memberships entity, advanced on payment. */
const MEMBER_MEMBERSHIPS_ENTITY_KEY = 'main.member_memberships';

/**
 * Permission required to record a payment. Read-only sessions still see the
 * recorded-payment list but get no recording controls.
 */
const MEMBERS_WRITE = 'main.members.write';

/**
 * Path of the sidecar record-payment workflow endpoint. Same-origin in hosted
 * mode, matching how `AppShellProvider` infers its GraphQL endpoint from the
 * document origin (and how `EventCheckin` reaches its sibling endpoint).
 */
const RECORD_PAYMENT_ENDPOINT = '/workflows/membership/record-payment';

/**
 * Payment-method vocabulary for the `method` select. The overlay declares the
 * field as a `select` but carries no enum option set (the same pattern as
 * `member_memberships.status`), so the vocabulary is supplied here. New
 * payments default to `card`.
 */
const PAYMENT_METHODS = ['card', 'cash', 'check'];

/**
 * Most open invoices / recorded payments one list loads. A limit-less query
 * would stop at the server's 100-row default window without saying so; this
 * screen names its window and compares it with the envelope's unpaged `total`,
 * showing a notice when rows were left out.
 */
const LIST_ROW_CAP = 500;

/** Shape of a dues_payments row as loaded from GraphQL. */
interface PaymentRow {
  id: number | string;
  invoice_id: number | string;
  amount_cents: number | null;
  paid_on: string | null;
  method: string | null;
}

/** Shape of a dues_invoices row as loaded from GraphQL. */
interface InvoiceRow {
  id: number | string;
  member_id: number | string;
  member_membership_id: number | string | null;
  amount_cents: number | null;
  status: string | null;
}

/** Shape of a members row as loaded from GraphQL. */
interface MemberRow {
  id: number | string;
  first_name: string | null;
  last_name: string | null;
}

/** Distinct ids, as a stable array for a filter `_in` list. */
function distinctIds(ids: Array<number | string | null | undefined>) {
  return [...new Set(ids.filter((id) => id != null).map(String))]
    .sort()
    .map((id) => (/^\d+$/.test(id) ? Number(id) : id));
}

/** The operator-facing text for a refused record-payment request. */
async function workflowFailureMessage(response: Response): Promise<string> {
  if (response.status === 403) {
    return 'You are not permitted to record this payment.';
  }
  if (response.status === 404) {
    return 'That invoice no longer exists.';
  }
  if (response.status === 400) {
    const text = await response.text();
    return text ? text.replace(/^"|"$/g, '') : 'The payment was rejected.';
  }
  return `Recording the payment failed (HTTP ${response.status}).`;
}

/**
 * Record-payment screen.
 *
 * Lists the most recent `main.dues_payments` rows, resolving each `invoice_id`
 * through `main.dues_invoices` to the billed member's display name — raw FK
 * ids are never shown, including for invoices already paid. Recording a
 * payment uses a {@link FieldControl} `fk-lookup` `<select>` over open
 * invoices (each labelled by member name and invoiced amount, never a raw
 * id), plus `amount_cents`, `paid_on`, and `method`.
 *
 * Recording is ONE request to `POST /workflows/membership/record-payment`,
 * which runs the whole operation server-side through the workflow executor:
 * it inserts the `dues_payments` row, marks the invoice `paid`, advances the
 * linked `member_memberships` row to `active`, and writes a
 * `payment.recorded` audit entry. Every internal write runs through the same
 * GraphQL mutation pipeline (`tenant-filter`, policy engine), and the
 * endpoint pre-flights the policy and refuses a non-positive amount before
 * any write. The screen never edits those tables directly, so a paid invoice
 * cannot stay `open` and be paid again. The Record button is disabled while
 * the request is in flight, so a double-click records one payment.
 *
 * `amount_cents` is a finance column (`policy-read-deny` for non-finance
 * roles, `write-requires: dues.manage` on payments), and the workflow needs
 * it, so recording is offered only to sessions holding both
 * `main.members.write` and `main.members.finance`.
 *
 * Payment-provider integration (charging a card) is out of scope: this
 * screen records a payment that has already been collected. A provider
 * adapter would run inside the workflow endpoint, before the insert.
 *
 * Must be mounted within an `AppShellProvider`, inside `AppLayout` /
 * `ProtectedRoute`.
 */
export function RecordPayment() {
  const { entities, isLoading: metadataLoading, isError, error } =
    useAppMetadata();
  const { permissions } = useSession();

  const canWrite = permissions.includes(MEMBERS_WRITE);

  // `amount_cents` on dues_invoices / dues_payments is a finance column the
  // host carries a `policy-read-deny` on. Non-finance sessions must not query
  // or render it; only finance_manager / admin (which hold
  // `main.members.finance`) see the amounts.
  const canReadFinance = canReadFinanceFields(permissions);
  const canRecord = canWrite && canReadFinance;

  const paymentEntity = entities[DUES_PAYMENTS_ENTITY_KEY];
  const paymentQueryName = useMemo(
    () => entityKeyToQueryName(DUES_PAYMENTS_ENTITY_KEY),
    [],
  );
  const invoiceQueryName = useMemo(
    () => entityKeyToQueryName(DUES_INVOICES_ENTITY_KEY),
    [],
  );
  const membersQueryName = useMemo(
    () => entityKeyToQueryName(MEMBERS_ENTITY_KEY),
    [],
  );
  const membershipQueryName = useMemo(
    () => entityKeyToQueryName(MEMBER_MEMBERSHIPS_ENTITY_KEY),
    [],
  );

  // Most recent recorded payments. `amount_cents` is requested only for
  // finance sessions — a non-finance session would have the column
  // read-denied.
  const paymentsQuery = useBifrostQuery<PaymentRow[]>(paymentQueryName, {
    fields: [
      'id',
      'invoice_id',
      ...(canReadFinance ? (['amount_cents'] as const) : []),
      'paid_on',
      'method',
    ],
    sort: [{ field: 'paid_on', direction: 'desc' }],
    pagination: { limit: LIST_ROW_CAP },
  });
  const payments = useMemo(
    () => paymentsQuery.data ?? [],
    [paymentsQuery.data],
  );

  // Open invoices for the fk-lookup select. Only open invoices can take a
  // payment, mirroring the overlay's `status = open` default grid filter.
  const invoicesQuery = useBifrostQuery<InvoiceRow[]>(invoiceQueryName, {
    fields: [
      'id',
      'member_id',
      'member_membership_id',
      ...(canReadFinance ? (['amount_cents'] as const) : []),
      'status',
    ],
    filter: { status: { _eq: 'open' } },
    pagination: { limit: LIST_ROW_CAP },
  });
  const invoices = useMemo(
    () => invoicesQuery.data ?? [],
    [invoicesQuery.data],
  );

  // The invoices the listed payments were made against — usually `paid`, so
  // absent from the open list. Loaded by id so each payment row can name its
  // member.
  const paymentInvoiceIds = useMemo(
    () => distinctIds(payments.map((p) => p.invoice_id)),
    [payments],
  );
  const paymentInvoicesQuery = useBifrostQuery<
    Array<Pick<InvoiceRow, 'id' | 'member_id'>>
  >(invoiceQueryName, {
    fields: ['id', 'member_id'],
    filter: { id: { _in: paymentInvoiceIds } },
    pagination: { limit: Math.max(paymentInvoiceIds.length, 1) },
    enabled: paymentInvoiceIds.length > 0,
  });
  const paymentInvoices = useMemo(
    () => paymentInvoicesQuery.data ?? [],
    [paymentInvoicesQuery.data],
  );

  // Members, to label invoices and payments by the billed member's name —
  // only the members those invoices name, never the whole roster.
  const memberIds = useMemo(
    () =>
      distinctIds([
        ...invoices.map((inv) => inv.member_id),
        ...paymentInvoices.map((inv) => inv.member_id),
      ]),
    [invoices, paymentInvoices],
  );
  const membersQuery = useBifrostQuery<MemberRow[]>(membersQueryName, {
    fields: ['id', 'first_name', 'last_name'],
    filter: { id: { _in: memberIds } },
    pagination: { limit: Math.max(memberIds.length, 1) },
    enabled: memberIds.length > 0,
  });
  const members = useMemo(
    () => membersQuery.data ?? [],
    [membersQuery.data],
  );

  const memberLabel = (memberId: number | string | null | undefined) => {
    const member = members.find((m) => String(m.id) === String(memberId));
    if (!member) {
      return String(memberId ?? '');
    }
    return [member.first_name, member.last_name]
      .filter((v) => v != null)
      .join(' ');
  };

  const invoiceById = (invoiceId: number | string | null | undefined) =>
    invoices.find((inv) => String(inv.id) === String(invoiceId)) ??
    paymentInvoices.find((inv) => String(inv.id) === String(invoiceId));

  // Payment rows label their invoice by the billed member's name.
  const paymentInvoiceLabel = (
    invoiceId: number | string | null | undefined,
  ) => {
    const invoice = invoiceById(invoiceId);
    if (!invoice) {
      return String(invoiceId ?? '');
    }
    return memberLabel(invoice.member_id);
  };

  // Open invoices in the fk-lookup select are labelled by member — and, for
  // finance sessions, the invoiced amount. The member-name lookup is inlined
  // (rather than calling `memberLabel`) so the memo's dependencies are the
  // source lists it actually reads.
  const invoiceOptions = useMemo(
    () =>
      invoices.map((invoice) => {
        const member = members.find(
          (m) => String(m.id) === String(invoice.member_id),
        );
        const name = member
          ? [member.first_name, member.last_name]
              .filter((v) => v != null)
              .join(' ')
          : String(invoice.member_id);
        return {
          key: String(invoice.id),
          label: canReadFinance
            ? `${name} — ${invoice.amount_cents ?? 0}`
            : name,
        };
      }),
    [invoices, members, canReadFinance],
  );

  // Memberships, so the status shown after a payment is the row the server
  // actually holds — read back after the workflow, never assumed `active`.
  const membershipsQuery = useBifrostQuery<
    Array<{ id: number | string; status: string | null }>
  >(membershipQueryName, {
    fields: ['id', 'status'],
    enabled: canRecord,
  });
  const memberships = useMemo(
    () => membershipsQuery.data ?? [],
    [membershipsQuery.data],
  );

  // Record-form state.
  const [newInvoiceId, setNewInvoiceId] = useState<unknown>('');
  const [newAmount, setNewAmount] = useState<unknown>('');
  const [newPaidOn, setNewPaidOn] = useState<unknown>('');
  const [newMethod, setNewMethod] = useState<unknown>('card');
  const [recording, setRecording] = useState(false);

  const feedback = useWriteFeedback();

  // The membership whose status is surfaced after the most recent recorded
  // payment. Only the id is held locally — the status itself is read back off
  // the server, never invented here.
  const [paidMembershipId, setPaidMembershipId] = useState<
    number | string | null
  >(null);
  const lastStatus =
    paidMembershipId === null
      ? null
      : (memberships.find((m) => String(m.id) === String(paidMembershipId))
          ?.status ?? null);

  const handleRecord = async () => {
    if (!canRecord || !newInvoiceId || recording) {
      return;
    }
    // Only an open invoice can take a payment, so look in the open list.
    const invoice = invoices.find(
      (inv) => String(inv.id) === String(newInvoiceId),
    );
    const amountCents = Number(newAmount);

    setRecording(true);
    try {
      const recorded = await feedback.run(async () => {
        // Mirrors the endpoint's own `AmountCents <= 0` refusal so the
        // operator gets the reason without a round trip.
        if (!Number.isSafeInteger(amountCents) || amountCents <= 0) {
          throw new Error('Enter a payment amount in cents greater than 0.');
        }
        const response = await fetch(RECORD_PAYMENT_ENDPOINT, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            invoiceId: Number(newInvoiceId),
            amountCents,
            paidOn: newPaidOn || null,
            method: newMethod || null,
          }),
        });
        if (!response.ok) {
          throw new Error(await workflowFailureMessage(response));
        }
      }, 'Payment recorded.');

      // A refused request wrote nothing; the operator's entered values are
      // kept so the write can be retried.
      if (!recorded) {
        return;
      }

      setPaidMembershipId(invoice?.member_membership_id ?? null);
      setNewInvoiceId('');
      setNewAmount('');
      setNewPaidOn('');
      setNewMethod('card');
      await Promise.all([
        paymentsQuery.refetch(),
        invoicesQuery.refetch(),
        membershipsQuery.refetch(),
      ]);
    } finally {
      setRecording(false);
    }
  };

  if (metadataLoading) {
    return <p data-testid="record-payment-loading">Loading…</p>;
  }

  if (isError) {
    return (
      <p role="alert" data-testid="record-payment-error">
        Failed to load app metadata: {error?.message}
      </p>
    );
  }

  if (!paymentEntity) {
    return (
      <p role="alert" data-testid="record-payment-missing">
        The dues_payments entity is not declared in the app-metadata overlay.
      </p>
    );
  }

  const paymentsTotal = paymentsQuery.total;
  const invoicesTotal = invoicesQuery.total;

  return (
    <section data-testid="record-payment">
      <h2>{paymentEntity.label ?? 'Dues Payments'}</h2>

      {paymentsTotal !== undefined && paymentsTotal > payments.length ? (
        <p role="status" data-testid="record-payment-payments-truncated">
          Showing the {payments.length} most recent of {paymentsTotal}{' '}
          payments.
        </p>
      ) : null}

      <ul data-testid="record-payment-list">
        {payments.map((payment) => (
          <li
            key={String(payment.id)}
            data-testid={`record-payment-${payment.id}`}
          >
            <span>{paymentInvoiceLabel(payment.invoice_id)}</span>
            {' — '}
            {canReadFinance ? (
              <>
                <span data-testid={`record-payment-${payment.id}-amount`}>
                  {payment.amount_cents}
                </span>
                {' — '}
              </>
            ) : null}
            <span>{payment.paid_on}</span>
            {' — '}
            <span>{payment.method}</span>
          </li>
        ))}
      </ul>

      <WriteFeedbackRegion
        feedback={feedback}
        testId="record-payment-write"
      />

      {lastStatus ? (
        <p data-testid="record-payment-status">
          Membership status: {lastStatus}
        </p>
      ) : null}

      {canWrite && !canReadFinance ? (
        <p data-testid="record-payment-finance-required">
          Recording a payment requires finance access.
        </p>
      ) : null}

      {canRecord ? (
        <div data-testid="record-payment-add">
          {invoicesTotal !== undefined && invoicesTotal > invoices.length ? (
            <p role="status" data-testid="record-payment-invoices-truncated">
              Showing the first {invoices.length} of {invoicesTotal} open
              invoices.
            </p>
          ) : null}
          <FieldControl
            name="invoice_id"
            field={paymentEntity.fields?.invoice_id}
            label="invoice_id"
            value={newInvoiceId}
            fkOptions={invoiceOptions}
            fkTargetEntity={DUES_INVOICES_ENTITY_KEY}
            onChange={(value) => setNewInvoiceId(value)}
          />
          <FieldControl
            name="amount_cents"
            field={paymentEntity.fields?.amount_cents}
            label="amount_cents"
            value={newAmount}
            onChange={(value) => setNewAmount(value)}
          />
          <FieldControl
            name="paid_on"
            field={paymentEntity.fields?.paid_on}
            label="paid_on"
            value={newPaidOn}
            onChange={(value) => setNewPaidOn(value)}
          />
          <FieldControl
            name="method"
            field={paymentEntity.fields?.method}
            label="method"
            value={newMethod}
            enumOptions={PAYMENT_METHODS}
            onChange={(value) => setNewMethod(value)}
          />
          <button
            type="button"
            disabled={recording}
            onClick={() => {
              void handleRecord();
            }}
          >
            {recording ? 'Recording…' : 'Record payment'}
          </button>
        </div>
      ) : null}
    </section>
  );
}
