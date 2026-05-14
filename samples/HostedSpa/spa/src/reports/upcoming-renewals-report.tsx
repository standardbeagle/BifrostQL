import { DuesReport } from './dues-report';

/** Qualified entity key of the member_memberships entity in the overlay. */
const MEMBER_MEMBERSHIPS_ENTITY_KEY = 'main.member_memberships';

/**
 * Near-term renewal window, in days. A membership counts as "upcoming" when
 * its `end_date` falls between today and today + this many days. Kept as a
 * single named constant so the window is configurable in one place.
 */
const RENEWAL_WINDOW_DAYS = 30;

/** Today's date as an ISO `YYYY-MM-DD` string. */
function today(): string {
  return new Date().toISOString().slice(0, 10);
}

/** Today + `RENEWAL_WINDOW_DAYS` as an ISO `YYYY-MM-DD` string. */
function windowEnd(): string {
  const end = new Date();
  end.setDate(end.getDate() + RENEWAL_WINDOW_DAYS);
  return end.toISOString().slice(0, 10);
}

/**
 * Upcoming renewals report.
 *
 * A read-only {@link DuesReport} over `main.member_memberships` filtered to
 * rows whose `end_date` (the renewal date) falls within the next
 * {@link RENEWAL_WINDOW_DAYS} days — `end_date >= today` and
 * `end_date <= today + window`. Lets an officer see which memberships are
 * about to come due for renewal.
 */
export function UpcomingRenewalsReport() {
  return (
    <DuesReport
      entityKey={MEMBER_MEMBERSHIPS_ENTITY_KEY}
      title="Upcoming Renewals"
      testId="upcoming-renewals-report"
      filter={{ end_date: { _gte: today(), _lte: windowEnd() } }}
    />
  );
}
