import { DuesReport } from './dues-report';

/** Qualified entity key of the member_memberships entity in the overlay. */
const MEMBER_MEMBERSHIPS_ENTITY_KEY = 'main.member_memberships';

/**
 * Expired memberships report.
 *
 * A read-only {@link DuesReport} over `main.member_memberships` filtered to
 * `status = expired` — the schema tracks a membership's lifecycle by its
 * `status` column, so an expired membership is one the renewal workflow (or an
 * officer) has marked `expired`. Lets an officer see which memberships have
 * lapsed past their renewal date.
 */
export function ExpiredMembershipsReport() {
  return (
    <DuesReport
      entityKey={MEMBER_MEMBERSHIPS_ENTITY_KEY}
      title="Expired Memberships"
      testId="expired-memberships-report"
      filter={{ status: { _eq: 'expired' } }}
    />
  );
}
