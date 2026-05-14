import { useMemo, useState } from 'react';
import {
  FieldControl,
  entityKeyToQueryName,
  useAppMetadata,
  useSession,
} from '@bifrostql/app-shell';
import {
  useBifrostQuery,
  useBifrostMutation,
  buildInsertMutation,
  buildUpdateMutation,
} from '@bifrostql/react';

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
 * Payment-method vocabulary for the `method` select. The overlay declares the
 * field as a `select` but carries no enum option set (the same pattern as
 * `member_memberships.status`), so the vocabulary is supplied here. New
 * payments default to `card`.
 */
const PAYMENT_METHODS = ['card', 'cash', 'check'];

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

/**
 * Record-payment screen.
 *
 * Lists every recorded `main.dues_payments` row, resolving each `invoice_id`
 * through `main.dues_invoices` to the billed member's display name — raw FK
 * ids are never shown. Recording a payment uses a {@link FieldControl}
 * `fk-lookup` `<select>` over open invoices (each labelled by member name and
 * invoiced amount, never a raw id), plus `amount_cents`, `paid_on`, and
 * `method`.
 *
 * Recording routes through two Bifrost mutations on the standard pipeline: an
 * `insert` on `dues_payments`, then an `update` on the invoice's
 * `member_memberships` row advancing its `status` to `active`. The updated
 * status is surfaced back on the screen so an officer sees the membership
 * become current. Both mutations run through `tenant-filter` and the policy
 * engine exactly as a direct GraphQL mutation would.
 *
 * Payment-provider integration (charging a card, reconciling an external
 * processor) is intentionally out of scope here: this screen records a
 * payment that has already been collected. A server-side workflow endpoint
 * that orchestrates a real provider is the documented future adapter point —
 * see `docs/src/content/docs/guides/workflow-mutations.md`.
 *
 * Mirrors the `MemberPlanAssignment` FK-free relationship-editor pattern. Must
 * be mounted within an `AppShellProvider`, inside `AppLayout` /
 * `ProtectedRoute`.
 */
export function RecordPayment() {
  const { entities, isLoading: metadataLoading, isError, error } =
    useAppMetadata();
  const { permissions } = useSession();

  const canWrite = permissions.includes(MEMBERS_WRITE);

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

  // Existing recorded payments.
  const paymentsQuery = useBifrostQuery<PaymentRow[]>(paymentQueryName, {
    fields: ['id', 'invoice_id', 'amount_cents', 'paid_on', 'method'],
  });
  const payments = paymentsQuery.data ?? [];

  // Open invoices for the fk-lookup select. Only open invoices can take a
  // payment, mirroring the overlay's `status = open` default grid filter.
  const invoicesQuery = useBifrostQuery<InvoiceRow[]>(invoiceQueryName, {
    fields: [
      'id',
      'member_id',
      'member_membership_id',
      'amount_cents',
      'status',
    ],
    filter: { status: { _eq: 'open' } },
  });
  const invoices = useMemo(
    () => invoicesQuery.data ?? [],
    [invoicesQuery.data],
  );

  // Members, to label invoices and payments by the billed member's name.
  const membersQuery = useBifrostQuery<Array<Record<string, unknown>>>(
    membersQueryName,
    { fields: ['id', 'first_name', 'last_name'] },
  );
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
    invoices.find((inv) => String(inv.id) === String(invoiceId));

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

  // Open invoices in the fk-lookup select are labelled by member + amount.
  // The member-name lookup is inlined (rather than calling `memberLabel`) so
  // the memo's dependencies are the two source lists it actually reads.
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
          label: `${name} — ${invoice.amount_cents ?? 0}`,
        };
      }),
    [invoices, members],
  );

  const insertPayment = useBifrostMutation(
    buildInsertMutation(paymentQueryName),
    { invalidateQueries: [paymentQueryName, invoiceQueryName] },
  );
  const updateMembership = useBifrostMutation(
    buildUpdateMutation(membershipQueryName),
    { invalidateQueries: [membershipQueryName] },
  );

  // Record-form state.
  const [newInvoiceId, setNewInvoiceId] = useState<unknown>('');
  const [newAmount, setNewAmount] = useState<unknown>('');
  const [newPaidOn, setNewPaidOn] = useState<unknown>('');
  const [newMethod, setNewMethod] = useState<unknown>('card');

  // The membership status surfaced after the most recent recorded payment.
  const [lastStatus, setLastStatus] = useState<string | null>(null);

  const handleRecord = () => {
    if (!canWrite || !newInvoiceId) {
      return;
    }
    const invoice = invoiceById(String(newInvoiceId));

    insertPayment.mutate({
      detail: {
        invoice_id: newInvoiceId,
        amount_cents: newAmount === '' ? null : Number(newAmount),
        paid_on: newPaidOn || null,
        method: newMethod || null,
      },
    });

    // Advance the invoice's membership to active: a recorded payment makes the
    // member current. Skipped when the invoice has no linked membership.
    if (invoice?.member_membership_id != null) {
      updateMembership.mutate({
        detail: { id: invoice.member_membership_id, status: 'active' },
      });
      setLastStatus('active');
    }

    setNewInvoiceId('');
    setNewAmount('');
    setNewPaidOn('');
    setNewMethod('card');
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

  return (
    <section data-testid="record-payment">
      <h2>{paymentEntity.label ?? 'Dues Payments'}</h2>

      <ul data-testid="record-payment-list">
        {payments.map((payment) => (
          <li
            key={String(payment.id)}
            data-testid={`record-payment-${payment.id}`}
          >
            <span>{paymentInvoiceLabel(payment.invoice_id)}</span>
            {' — '}
            <span>{payment.amount_cents}</span>
            {' — '}
            <span>{payment.paid_on}</span>
            {' — '}
            <span>{payment.method}</span>
          </li>
        ))}
      </ul>

      {lastStatus ? (
        <p data-testid="record-payment-status">
          Membership status: {lastStatus}
        </p>
      ) : null}

      {canWrite ? (
        <div data-testid="record-payment-add">
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
          <button type="button" onClick={handleRecord}>
            Record payment
          </button>
        </div>
      ) : null}
    </section>
  );
}
