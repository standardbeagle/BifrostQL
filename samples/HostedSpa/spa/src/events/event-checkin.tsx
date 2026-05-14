import { useMemo, useState } from 'react';
import { useNavigate, useParams } from '@standardbeagle/virtual-router';
import {
  entityKeyToQueryName,
  useAppMetadata,
  useSession,
} from '@bifrostql/app-shell';
import { useBifrostQuery } from '@bifrostql/react';

/** Qualified entity key of the event_attendance entity in the overlay. */
const EVENT_ATTENDANCE_ENTITY_KEY = 'main.event_attendance';

/** Qualified entity key of the events entity, for the event title. */
const EVENTS_ENTITY_KEY = 'main.events';

/** Qualified entity key of the members entity, the check-in lookup target. */
const MEMBERS_ENTITY_KEY = 'main.members';

/**
 * Permission required to check members in. No distinct event-manager
 * permission is wired yet, so officers/admins are identified by the existing
 * `main.members.write` permission; read-only sessions still see the
 * already-checked-in list but get no check-in controls.
 */
const MEMBERS_WRITE = 'main.members.write';

/**
 * Path of the sidecar check-in workflow endpoint. Same-origin in hosted mode,
 * matching how `AppShellProvider` infers its GraphQL endpoint from the document
 * origin. The endpoint inserts `event_attendance` through
 * `IBifrostWorkflowExecutor` and writes an `event.checkin` audit row — the
 * check-in flow is a workflow mutation, never a raw `event_attendance` edit.
 */
const CHECK_IN_ENDPOINT = '/workflows/membership/check-in';

/** Shape of an event_attendance row as loaded from GraphQL. */
interface AttendanceRow {
  id: number | string;
  event_id: number | string;
  member_id: number | string;
  checked_in_at: string | null;
}

/** Shape of a members row as loaded from GraphQL. */
interface MemberRow {
  id: number | string;
  first_name: string | null;
  last_name: string | null;
}

/**
 * Tablet/mobile-friendly event check-in screen.
 *
 * Scoped to the event in the `:id` route param (`/events/:id/check-in`). Lists
 * every `main.event_attendance` row for the event, resolving each row's
 * `member_id` to the member's display name via `main.members` — the raw FK id
 * is never shown. Checking a member in is driven by a fast text filter over
 * the roster: typing a name narrows a list of large touch-target buttons, one
 * per member, and tapping a button calls the sidecar check-in workflow
 * endpoint. Members already checked in are not offered as check-in targets.
 *
 * The check-in itself routes through `POST /workflows/membership/check-in`,
 * which inserts `event_attendance` through `IBifrostWorkflowExecutor` and
 * writes an `event.checkin` audit row — exactly the record-payment /
 * renew-membership workflow-endpoint pattern. The screen never edits the
 * `event_attendance` table directly. A repeat check-in is rejected by the
 * endpoint's `UNIQUE(event_id, member_id)` guard as `409 Conflict`, surfaced
 * here as an inline message.
 *
 * Must be mounted within an `AppShellProvider`, inside `AppLayout` /
 * `ProtectedRoute`.
 */
export function EventCheckin() {
  const navigate = useNavigate();
  const params = useParams();
  const { entities, isLoading: metadataLoading, isError, error } =
    useAppMetadata();
  const { permissions } = useSession();

  const eventId = params.id;
  const canWrite = permissions.includes(MEMBERS_WRITE);

  const attendanceEntity = entities[EVENT_ATTENDANCE_ENTITY_KEY];
  const attendanceQueryName = useMemo(
    () => entityKeyToQueryName(EVENT_ATTENDANCE_ENTITY_KEY),
    [],
  );
  const eventsQueryName = useMemo(
    () => entityKeyToQueryName(EVENTS_ENTITY_KEY),
    [],
  );
  const membersQueryName = useMemo(
    () => entityKeyToQueryName(MEMBERS_ENTITY_KEY),
    [],
  );

  // Existing attendance rows for this event.
  const attendanceQuery = useBifrostQuery<AttendanceRow[]>(
    attendanceQueryName,
    {
      fields: ['id', 'event_id', 'member_id', 'checked_in_at'],
      filter: eventId ? { event_id: { _eq: eventId } } : undefined,
      enabled: !!eventId,
    },
  );
  const attendance = useMemo(
    () => attendanceQuery.data ?? [],
    [attendanceQuery.data],
  );

  // The event being checked in to, for the screen title.
  const eventQuery = useBifrostQuery<Array<Record<string, unknown>>>(
    eventsQueryName,
    {
      fields: ['id', 'title'],
      filter: eventId ? { id: { _eq: eventId } } : undefined,
      pagination: { limit: 1 },
      enabled: !!eventId,
    },
  );
  const eventRow = eventQuery.data?.[0];

  // The full member roster — the source for the fast check-in lookup.
  const membersQuery = useBifrostQuery<MemberRow[]>(membersQueryName, {
    fields: ['id', 'first_name', 'last_name'],
    enabled: !!eventId,
  });
  const members = useMemo(
    () => membersQuery.data ?? [],
    [membersQuery.data],
  );

  const memberLabel = (memberId: number | string | null | undefined) => {
    const member = members.find((m) => String(m.id) === String(memberId));
    if (!member) {
      return String(memberId ?? '');
    }
    return [member.first_name, member.last_name]
      .filter((v) => v != null)
      .join(' ');
  };

  // The set of member ids already checked in, so they drop out of the lookup.
  const checkedInIds = useMemo(
    () => new Set(attendance.map((row) => String(row.member_id))),
    [attendance],
  );

  // Fast member lookup: a text filter over the roster, excluding members who
  // are already checked in. An empty query lists the whole remaining roster.
  const [lookup, setLookup] = useState('');
  const candidates = useMemo(() => {
    const needle = lookup.trim().toLowerCase();
    return members
      .filter((m) => !checkedInIds.has(String(m.id)))
      .filter((m) => {
        if (!needle) {
          return true;
        }
        const name = [m.first_name, m.last_name]
          .filter((v) => v != null)
          .join(' ')
          .toLowerCase();
        return name.includes(needle);
      });
  }, [members, checkedInIds, lookup]);

  // Inline feedback for the most recent check-in attempt.
  const [message, setMessage] = useState<string | null>(null);
  const [pendingId, setPendingId] = useState<string | null>(null);

  const handleCheckIn = async (member: MemberRow) => {
    if (!canWrite || !eventId) {
      return;
    }
    setPendingId(String(member.id));
    setMessage(null);
    try {
      const response = await fetch(CHECK_IN_ENDPOINT, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          eventId: Number(eventId),
          memberId: Number(member.id),
        }),
      });
      if (response.status === 409) {
        setMessage(`${memberLabel(member.id)} is already checked in.`);
        return;
      }
      if (!response.ok) {
        setMessage(`Check-in failed for ${memberLabel(member.id)}.`);
        return;
      }
      setLookup('');
      setMessage(`${memberLabel(member.id)} checked in.`);
      await attendanceQuery.refetch();
    } catch {
      setMessage(`Check-in failed for ${memberLabel(member.id)}.`);
    } finally {
      setPendingId(null);
    }
  };

  if (metadataLoading) {
    return <p data-testid="event-checkin-loading">Loading…</p>;
  }

  if (isError) {
    return (
      <p role="alert" data-testid="event-checkin-error">
        Failed to load app metadata: {error?.message}
      </p>
    );
  }

  if (!attendanceEntity) {
    return (
      <p role="alert" data-testid="event-checkin-missing">
        The event_attendance entity is not declared in the app-metadata overlay.
      </p>
    );
  }

  const title = eventRow?.title
    ? `Check-in — ${String(eventRow.title)}`
    : (attendanceEntity.label ?? 'Event Check-in');

  return (
    <section data-testid="event-checkin" className="event-checkin">
      <h2>{title}</h2>

      <ul data-testid="event-checkin-list" className="event-checkin__attendance">
        {attendance.map((row) => (
          <li key={String(row.id)} data-testid={`event-checkin-${row.id}`}>
            <span>{memberLabel(row.member_id)}</span>
            {' — '}
            <span>{row.checked_in_at}</span>
          </li>
        ))}
      </ul>

      {message ? (
        <p data-testid="event-checkin-message" role="status">
          {message}
        </p>
      ) : null}

      {canWrite ? (
        <div data-testid="event-checkin-add" className="event-checkin__lookup">
          <label htmlFor="event-checkin-search">Find a member</label>
          <input
            id="event-checkin-search"
            type="search"
            className="event-checkin__search"
            placeholder="Type a name…"
            value={lookup}
            onChange={(e) => setLookup(e.target.value)}
          />
          <ul className="event-checkin__candidates">
            {candidates.map((member) => (
              <li key={String(member.id)}>
                <button
                  type="button"
                  className="event-checkin__candidate"
                  data-testid={`event-checkin-button-${member.id}`}
                  disabled={pendingId === String(member.id)}
                  onClick={() => handleCheckIn(member)}
                >
                  {[member.first_name, member.last_name]
                    .filter((v) => v != null)
                    .join(' ')}
                </button>
              </li>
            ))}
            {candidates.length === 0 ? (
              <li data-testid="event-checkin-no-candidates">
                No matching members to check in.
              </li>
            ) : null}
          </ul>
        </div>
      ) : null}

      <div className="event-checkin__actions">
        <button type="button" onClick={() => navigate('/events')}>
          Back to events
        </button>
      </div>
    </section>
  );
}
