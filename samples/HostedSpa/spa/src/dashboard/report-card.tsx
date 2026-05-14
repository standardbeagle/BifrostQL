import type { ReactNode } from 'react';

/** An optional link from a {@link ReportCard} to a matching report screen. */
export interface ReportCardLink {
  /**
   * Target route path (e.g. `/reports/unpaid-dues`). Rendered as a hash anchor
   * (`#<href>`), the same in-app link form `MembershipNav` uses in `App.tsx`.
   */
  href: string;
  /** Visible link text. */
  label: string;
}

/** Props for {@link ReportCard}. */
export interface ReportCardProps {
  /**
   * Stable test id for the card root. The loading placeholder is tagged
   * `<testId>-loading`.
   */
  testId: string;
  /** Metric label shown above the value (e.g. `Members`). */
  label: string;
  /**
   * The metric value. A pre-formatted string (e.g. a `$150.00` dues total) or
   * a raw number (e.g. a count). Suppressed while {@link ReportCardProps.isLoading}.
   */
  value: ReactNode;
  /** When `true`, the value is replaced by a loading placeholder. */
  isLoading?: boolean;
  /** Optional link to the report screen that backs this metric. */
  link?: ReportCardLink;
}

/**
 * A single dashboard summary card: a metric label, its value, and an optional
 * link to the matching report screen.
 *
 * Purely presentational — it owns no data fetching or permission logic. The
 * {@link import('./dashboard').Dashboard} screen fetches each metric via
 * `useBifrostQuery` (so tenant scoping applies server-side) and decides, per
 * session, whether finance values are shown, then passes the resolved `value`
 * and `isLoading` down. Keeping the card dumb lets it be unit-tested in
 * isolation and reused for every metric.
 */
export function ReportCard({
  testId,
  label,
  value,
  isLoading,
  link,
}: ReportCardProps) {
  return (
    <section data-testid={testId} aria-label={label}>
      <h3>{label}</h3>
      {isLoading ? (
        <p data-testid={`${testId}-loading`}>Loading…</p>
      ) : (
        <p>{value}</p>
      )}
      {link ? <a href={`#${link.href}`}>{link.label}</a> : null}
    </section>
  );
}
