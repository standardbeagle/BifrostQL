import { DuesReport } from './dues-report';

/** Qualified entity key of the dues_invoices entity in the overlay. */
const DUES_INVOICES_ENTITY_KEY = 'main.dues_invoices';

/**
 * Unpaid dues report.
 *
 * A read-only {@link DuesReport} over `main.dues_invoices` filtered to
 * `status = open` — the schema tracks an invoice's settlement by its `status`
 * column (`open` until a payment closes it), not a numeric balance, so an open
 * invoice *is* an outstanding amount owed. Each row shows the billed member
 * and the invoiced amount, so an officer can see who has unpaid dues.
 */
export function UnpaidDuesReport() {
  return (
    <DuesReport
      entityKey={DUES_INVOICES_ENTITY_KEY}
      title="Unpaid Dues"
      testId="unpaid-dues-report"
      filter={{ status: { _eq: 'open' } }}
    />
  );
}
