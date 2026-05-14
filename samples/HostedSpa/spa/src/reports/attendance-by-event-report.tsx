import { AttendanceReport } from './attendance-report';

/**
 * Attendance-by-event report.
 *
 * A read-only {@link AttendanceReport} over `main.event_attendance` sorted by
 * `event_id` so every check-in for the same event sits together — an officer
 * can scan the table to see who attended each event. The report is grouped by
 * its sort dimension rather than narrowed by a filter, so it covers all
 * attendance rows (tenant-scoped server-side).
 */
export function AttendanceByEventReport() {
  return (
    <AttendanceReport
      title="Attendance by Event"
      testId="attendance-by-event-report"
      sort={[
        { field: 'event_id', direction: 'asc' },
        { field: 'checked_in_at', direction: 'desc' },
      ]}
    />
  );
}
