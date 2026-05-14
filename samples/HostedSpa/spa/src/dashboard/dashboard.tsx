import { useSession } from '@bifrostql/app-shell';
import { useBifrostQuery } from '@bifrostql/react';
import { canReadFinanceFields } from '../membership-plans/finance-fields';
import { ReportCard } from './report-card';

/**
 * Active-member status flag. `MemberList`'s deactivate action sets a member's
 * `status` to `inactive`; an active member is therefore one whose `status` is
 * `active`. Kept as a named constant so the dashboard's active-count filter
 * stays in step with that flow.
 */
const ACTIVE_MEMBER_STATUS = 'active';

/**
 * Open-invoice status. `dues_invoices.status` is `open` until a payment closes
 * it — an open invoice is an outstanding amount owed. Mirrors the filter the
 * `UnpaidDuesReport` screen applies.
 */
const OPEN_INVOICE_STATUS = 'open';

/**
 * Near-term renewal window, in days — a membership counts as "upcoming" when
 * its `end_date` falls between today and today + this many days. Mirrors the
 * window the `UpcomingRenewalsReport` screen applies.
 */
const RENEWAL_WINDOW_DAYS = 30;

/** Today's date as an ISO `YYYY-MM-DD` string. */
function today(): string {
  return new Date().toISOString().slice(0, 10);
}

/** Today + `RENEWAL_WINDOW_DAYS` as an ISO `YYYY-MM-DD` string. */
function renewalWindowEnd(): string {
  const end = new Date();
  end.setDate(end.getDate() + RENEWAL_WINDOW_DAYS);
  return end.toISOString().slice(0, 10);
}

/** A row carrying only its `id` — all the count cards need from a query. */
interface IdRow {
  id: number | string;
}

/** A `dues_invoices` row; `amount_cents` is present only for finance sessions. */
interface DuesInvoiceRow extends IdRow {
  amount_cents?: number;
}

/** Format a cents integer as a `$d.dd` string. */
function formatCents(cents: number): string {
  return `$${(cents / 100).toFixed(2)}`;
}

/**
 * Membership-manager dashboard: four at-a-glance summary cards over the
 * existing overlay entities — active membership count, unpaid dues, upcoming
 * renewals, and recent attendance.
 *
 * Every metric is fetched through `useBifrostQuery`, so the host's
 * `tenant-filter` module scopes each count to the caller's tenant without any
 * client-side tenant predicate — exactly the path the report screens use. Each
 * card selects only `id` (plus `amount_cents` for the dues card on a finance
 * session), so the count is `data.length` and no query ever names a column the
 * host's `policy-read-deny` would reject.
 *
 * The dues card additionally shows the summed invoice amount, but only for a
 * finance session: `canReadFinanceFields` gates both whether `amount_cents` is
 * queried and whether the total is rendered, consistent with `dues-report.tsx`.
 *
 * Must be mounted within an `AppShellProvider`, inside `AppLayout` /
 * `ProtectedRoute`.
 */
export function Dashboard() {
  const { permissions } = useSession();
  const canReadFinance = canReadFinanceFields(permissions);

  // Active members — count only.
  const membersQuery = useBifrostQuery<IdRow[]>('main_members', {
    fields: ['id'],
    filter: { status: { _eq: ACTIVE_MEMBER_STATUS } },
  });

  // Open dues invoices — count, plus summed amount for finance sessions. A
  // non-finance session never selects `amount_cents`, so the host's
  // `policy-read-deny` is never tripped and no total is available to render.
  const duesQuery = useBifrostQuery<DuesInvoiceRow[]>('main_dues_invoices', {
    fields: canReadFinance ? ['id', 'amount_cents'] : ['id'],
    filter: { status: { _eq: OPEN_INVOICE_STATUS } },
  });

  // Memberships whose renewal date falls within the near-term window.
  const renewalsQuery = useBifrostQuery<IdRow[]>('main_member_memberships', {
    fields: ['id'],
    filter: { end_date: { _gte: today(), _lte: renewalWindowEnd() } },
  });

  // Recent attendance — count of all attendance rows (tenant-scoped server-side).
  const attendanceQuery = useBifrostQuery<IdRow[]>('main_event_attendance', {
    fields: ['id'],
  });

  const memberCount = membersQuery.data?.length ?? 0;
  const duesRows = duesQuery.data ?? [];
  const duesCount = duesRows.length;
  const renewalCount = renewalsQuery.data?.length ?? 0;
  const attendanceCount = attendanceQuery.data?.length ?? 0;

  // The dues card's value: for a finance session, the count followed by the
  // summed amount; for a non-finance session, just the count.
  const duesTotalCents = canReadFinance
    ? duesRows.reduce((sum, row) => sum + (row.amount_cents ?? 0), 0)
    : null;
  const duesValue =
    duesTotalCents === null
      ? duesCount
      : `${duesCount} (${formatCents(duesTotalCents)})`;

  return (
    <section data-testid="dashboard">
      <h2>Dashboard</h2>
      <div>
        <ReportCard
          testId="dashboard-members-card"
          label="Active Members"
          value={memberCount}
          isLoading={membersQuery.isLoading}
        />
        <ReportCard
          testId="dashboard-dues-card"
          label="Unpaid Dues"
          value={duesValue}
          isLoading={duesQuery.isLoading}
          link={{ href: '/reports/unpaid-dues', label: 'View report' }}
        />
        <ReportCard
          testId="dashboard-renewals-card"
          label="Upcoming Renewals"
          value={renewalCount}
          isLoading={renewalsQuery.isLoading}
          link={{
            href: '/reports/upcoming-renewals',
            label: 'View report',
          }}
        />
        <ReportCard
          testId="dashboard-attendance-card"
          label="Recent Attendance"
          value={attendanceCount}
          isLoading={attendanceQuery.isLoading}
          link={{
            href: '/reports/attendance-by-event',
            label: 'View report',
          }}
        />
      </div>
    </section>
  );
}
