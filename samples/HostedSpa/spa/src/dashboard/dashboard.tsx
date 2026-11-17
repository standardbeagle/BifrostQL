import { useSession } from '@bifrostql/app-shell';
import { useBifrost, useBifrostQuery } from '@bifrostql/react';
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

/** A row carrying only its `id` — the cheapest selection a count query needs. */
interface IdRow {
  id: number | string;
}

/**
 * Row window for a count-only query. The paged envelope's `total` is the
 * server's unpaged `COUNT(*)` under the same tenant/policy filters, computed
 * whatever the window — so the cards ask for zero rows and read `total`.
 */
const COUNT_ONLY = { limit: 0 } as const;

/** GraphQL root field of the open-invoice whole-table aggregate. */
const DUES_INVOICES_QUERY = 'main_dues_invoices';

/**
 * Open-invoice amount total, summed server-side by `<t>Aggregate` over EVERY
 * open invoice. The table name and filter value are constants, never user
 * input. Issued only for finance sessions: `_sum { amount_cents }` reads the
 * policy-read-denied column.
 */
const OPEN_DUES_SUM_QUERY = `{ ${DUES_INVOICES_QUERY}Aggregate(filter: { status: { _eq: "${OPEN_INVOICE_STATUS}" } }) { _sum { amount_cents } } }`;

/** Response shape of {@link OPEN_DUES_SUM_QUERY}: one whole-table row. */
interface OpenDuesSumResponse {
  main_dues_invoicesAggregate: Array<{
    _sum: { amount_cents: number | null } | null;
  }>;
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
 * Every metric comes from the server, never from counting or summing fetched
 * rows: a row query returns at most the server's row window (100 rows when no
 * `limit` is named), so `data.length` would cap every card at 100. Counts read
 * the paged envelope's `total` from a zero-row query; the finance-only dues
 * amount comes from `main_dues_invoicesAggregate { _sum { amount_cents } }`.
 * Both go through the GraphQL door, so the host's `tenant-filter` scopes each
 * metric to the caller's tenant exactly as the report screens are scoped.
 *
 * `canReadFinanceFields` gates whether the dues sum is queried at all and
 * whether it is rendered, consistent with `dues-report.tsx`: a non-finance
 * session never names `amount_cents`, so the host's `policy-read-deny` is
 * never tripped.
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
    pagination: COUNT_ONLY,
  });

  // Open dues invoices — count for every session.
  const duesQuery = useBifrostQuery<IdRow[]>(DUES_INVOICES_QUERY, {
    fields: ['id'],
    filter: { status: { _eq: OPEN_INVOICE_STATUS } },
    pagination: COUNT_ONLY,
  });

  // Open dues amount — finance sessions only.
  const duesSumQuery = useBifrost<OpenDuesSumResponse>(
    OPEN_DUES_SUM_QUERY,
    undefined,
    { enabled: canReadFinance },
  );

  // Memberships whose renewal date falls within the near-term window.
  const renewalsQuery = useBifrostQuery<IdRow[]>('main_member_memberships', {
    fields: ['id'],
    filter: { end_date: { _gte: today(), _lte: renewalWindowEnd() } },
    pagination: COUNT_ONLY,
  });

  // Recent attendance — count of all attendance rows (tenant-scoped server-side).
  const attendanceQuery = useBifrostQuery<IdRow[]>('main_event_attendance', {
    fields: ['id'],
    pagination: COUNT_ONLY,
  });

  // A count is rendered only from an answer that actually arrived. A failed
  // query is reported by the card's `isError` branch — coalescing a missing
  // count to `0` would tell an operator "none" when the truth is "unknown",
  // and on the dues card that reads as "nothing is owed".
  const memberCount = membersQuery.total;
  const duesCount = duesQuery.total;
  const renewalCount = renewalsQuery.total;
  const attendanceCount = attendanceQuery.total;

  // The dues card's value: for a finance session, the count followed by the
  // server-summed amount; for a non-finance session, just the count. An
  // aggregate over zero open invoices sums to null, which is $0.00 owed.
  const duesSumRow = duesSumQuery.data?.main_dues_invoicesAggregate[0];
  const duesTotalCents =
    canReadFinance && duesSumRow !== undefined
      ? (duesSumRow._sum?.amount_cents ?? 0)
      : null;
  const duesValue =
    duesTotalCents === null || duesCount === undefined
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
          isError={membersQuery.isError}
        />
        <ReportCard
          testId="dashboard-dues-card"
          label="Unpaid Dues"
          value={duesValue}
          isLoading={duesQuery.isLoading || duesSumQuery.isLoading}
          isError={duesQuery.isError || duesSumQuery.isError}
          link={{ href: '/reports/unpaid-dues', label: 'View report' }}
        />
        <ReportCard
          testId="dashboard-renewals-card"
          label="Upcoming Renewals"
          value={renewalCount}
          isLoading={renewalsQuery.isLoading}
          isError={renewalsQuery.isError}
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
          isError={attendanceQuery.isError}
          link={{
            href: '/reports/attendance-by-event',
            label: 'View report',
          }}
        />
      </div>
    </section>
  );
}
