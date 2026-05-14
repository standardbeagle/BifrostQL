import { useMemo } from 'react';
import {
  buildColumns,
  entityKeyToQueryName,
  useAppMetadata,
} from '@bifrostql/app-shell';
import { BifrostTable } from '@bifrostql/react';
import type { TableFilter } from '@bifrostql/react';

/** Props for {@link DuesReport}. */
export interface DuesReportProps {
  /** Qualified entity key in the app-metadata overlay (e.g. `main.dues_invoices`). */
  entityKey: string;
  /** Heading shown above the report table. */
  title: string;
  /**
   * Stable test id prefix. The section, and its loading/error/missing states,
   * are tagged `<testId>`, `<testId>-loading`, etc.
   */
  testId: string;
  /**
   * Server-side filter applied to the table. Tenant scoping is enforced by the
   * `tenant-filter` module on the host, so this only narrows the report to its
   * subject rows (open invoices, near-term renewals, expired memberships).
   */
  filter: TableFilter;
}

/**
 * Read-only, filtered `BifrostTable` view over an existing overlay entity —
 * the shared body of the three dues/membership reports.
 *
 * Columns are derived from the entity's overlay metadata via
 * {@link buildColumns}, exactly like {@link import('../members/member-list').MemberList}
 * and {@link import('../membership-plans/plan-list').PlanList}; the report only
 * supplies a fixed `defaultFilters` and renders no row actions, so it is
 * strictly read-only. Tenant scoping is applied server-side by the host's
 * `tenant-filter` module, so every report is tenant-scoped without any
 * client-side tenant predicate.
 *
 * Must be mounted within an `AppShellProvider`, inside `AppLayout` /
 * `ProtectedRoute`.
 */
export function DuesReport({ entityKey, title, testId, filter }: DuesReportProps) {
  const { entities, isLoading, isError, error } = useAppMetadata();

  const entity = entities[entityKey];
  const queryName = useMemo(
    () => entityKeyToQueryName(entityKey),
    [entityKey],
  );
  const columns = useMemo(
    () => (entity ? buildColumns(entity) : []),
    [entity],
  );

  if (isLoading) {
    return <p data-testid={`${testId}-loading`}>Loading {title}…</p>;
  }

  if (isError) {
    return (
      <p role="alert" data-testid={`${testId}-error`}>
        Failed to load app metadata: {error?.message}
      </p>
    );
  }

  if (!entity) {
    return (
      <p role="alert" data-testid={`${testId}-missing`}>
        The {entityKey} entity is not declared in the app-metadata overlay.
      </p>
    );
  }

  return (
    <section data-testid={testId}>
      <h2>{title}</h2>
      <BifrostTable
        query={queryName}
        columns={columns}
        defaultFilters={filter}
      />
    </section>
  );
}
