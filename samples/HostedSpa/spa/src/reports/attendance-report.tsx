import { useMemo } from 'react';
import {
  buildColumns,
  entityKeyToQueryName,
  useAppMetadata,
} from '@bifrostql/app-shell';
import { BifrostTable } from '@bifrostql/react';
import type { SortOption } from '@bifrostql/react';
import { ExportButton } from '../exports/export-button';

/** Props for {@link AttendanceReport}. */
export interface AttendanceReportProps {
  /** Heading shown above the report table. */
  title: string;
  /**
   * Stable test id prefix. The section, and its loading/error/missing states,
   * are tagged `<testId>`, `<testId>-loading`, etc.
   */
  testId: string;
  /**
   * Server-side sort applied to the table. The two attendance reports differ
   * only by which dimension they group on — by event or by member — so the
   * sort field is the report's distinguishing input. Tenant scoping is
   * enforced server-side by the host's `tenant-filter` module.
   */
  sort: SortOption[];
}

/** Qualified entity key of the event_attendance entity in the overlay. */
const EVENT_ATTENDANCE_ENTITY_KEY = 'main.event_attendance';

/**
 * Read-only, sorted `BifrostTable` view over `main.event_attendance` — the
 * shared body of the two event-attendance reports.
 *
 * Columns are derived from the entity's overlay metadata via
 * {@link buildColumns}, exactly like
 * {@link import('./dues-report').DuesReport}; the report only supplies a fixed
 * `defaultSort` and renders no row actions, so it is strictly read-only.
 * Grouping "by event" / "by member" is expressed as a server-side sort on the
 * `event_id` / `member_id` column so attendance rows for the same event or
 * member sit together. Tenant scoping is applied server-side by the host's
 * `tenant-filter` module, so every report is tenant-scoped without any
 * client-side tenant predicate.
 *
 * Must be mounted within an `AppShellProvider`, inside `AppLayout` /
 * `ProtectedRoute`.
 */
export function AttendanceReport({ title, testId, sort }: AttendanceReportProps) {
  const { entities, isLoading, isError, error } = useAppMetadata();

  const entity = entities[EVENT_ATTENDANCE_ENTITY_KEY];
  const queryName = useMemo(
    () => entityKeyToQueryName(EVENT_ATTENDANCE_ENTITY_KEY),
    [],
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
        The {EVENT_ATTENDANCE_ENTITY_KEY} entity is not declared in the
        app-metadata overlay.
      </p>
    );
  }

  return (
    <section data-testid={testId}>
      <h2>{title}</h2>
      <ExportButton
        queryName={queryName}
        columns={columns}
        sort={sort}
        fileName={testId}
        testId={`${testId}-export`}
      />
      <BifrostTable
        query={queryName}
        columns={columns}
        defaultSort={sort}
      />
    </section>
  );
}
