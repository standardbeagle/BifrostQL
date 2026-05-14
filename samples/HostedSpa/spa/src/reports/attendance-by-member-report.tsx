import { AttendanceReport } from './attendance-report';

/**
 * Attendance-by-member report.
 *
 * A read-only {@link AttendanceReport} over `main.event_attendance` sorted by
 * `member_id` so every check-in for the same member sits together — an officer
 * can scan the table to see which events each member attended. The report is
 * grouped by its sort dimension rather than narrowed by a filter, so it covers
 * all attendance rows (tenant-scoped server-side).
 */
export function AttendanceByMemberReport() {
  return (
    <AttendanceReport
      title="Attendance by Member"
      testId="attendance-by-member-report"
      sort={[
        { field: 'member_id', direction: 'asc' },
        { field: 'checked_in_at', direction: 'desc' },
      ]}
    />
  );
}
