import { useMemo, useState } from 'react';
import { useNavigate, useParams } from '@standardbeagle/virtual-router';
import {
  FieldControl,
  entityKeyToQueryName,
  useAppMetadata,
  useSession,
} from '@bifrostql/app-shell';
import {
  useBifrostQuery,
  useBifrostMutation,
  buildInsertMutation,
  buildUpdateMutation,
} from '@bifrostql/react';

/** Qualified entity key of the event_rsvps entity in the overlay. */
const EVENT_RSVPS_ENTITY_KEY = 'main.event_rsvps';

/** Qualified entity key of the events entity, for the event title. */
const EVENTS_ENTITY_KEY = 'main.events';

/** Qualified entity key of the members entity, the fk-lookup target. */
const MEMBERS_ENTITY_KEY = 'main.members';

/**
 * Permission required to record and edit RSVPs. No distinct event-manager
 * permission is wired yet, so officers/admins are identified by the existing
 * `main.members.write` permission; read-only sessions still see the RSVP list
 * but get no mutating controls.
 */
const MEMBERS_WRITE = 'main.members.write';

/**
 * RSVP responses for the `response` select. The overlay declares the field as
 * a `select` but does not carry an enum option set, so the response vocabulary
 * (per the overlay `helpText`: "yes, no, or maybe") is supplied here — the same
 * pattern `HouseholdMembers` uses for its `relationship` select.
 */
const RESPONSE_OPTIONS = ['yes', 'no', 'maybe'];

/** Shape of an event_rsvps row as loaded from GraphQL. */
interface RsvpRow {
  id: number | string;
  event_id: number | string;
  member_id: number | string;
  response: string | null;
  guests: number | null;
}

/**
 * Per-event RSVP management screen.
 *
 * Scoped to the event in the `:id` route param (`/events/:id/rsvps`). Lists
 * every `main.event_rsvps` row for the event, resolving each row's `member_id`
 * to the member's display name via the `main.members` entity — the raw FK id is
 * never shown or typed. Recording a new RSVP uses a {@link FieldControl}
 * `fk-lookup` `<select>` over candidate members plus `response` and `guests`
 * controls, so a new RSVP is created entirely by selection. Each existing row
 * offers inline `response` / `guests` controls (Save → update mutation). All
 * mutating controls are gated on `main.members.write`.
 *
 * Mirrors the `HouseholdMembers` relationship-row pattern. Must be mounted
 * within an `AppShellProvider`, inside `AppLayout` / `ProtectedRoute`.
 */
export function EventRsvps() {
  const navigate = useNavigate();
  const params = useParams();
  const { entities, isLoading: metadataLoading, isError, error } =
    useAppMetadata();
  const { permissions } = useSession();

  const eventId = params.id;
  const canWrite = permissions.includes(MEMBERS_WRITE);

  const rsvpEntity = entities[EVENT_RSVPS_ENTITY_KEY];
  const rsvpQueryName = useMemo(
    () => entityKeyToQueryName(EVENT_RSVPS_ENTITY_KEY),
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

  // Existing RSVPs for this event.
  const rsvpsQuery = useBifrostQuery<RsvpRow[]>(rsvpQueryName, {
    fields: ['id', 'event_id', 'member_id', 'response', 'guests'],
    filter: eventId ? { event_id: { _eq: eventId } } : undefined,
    enabled: !!eventId,
  });
  const rsvps = rsvpsQuery.data ?? [];

  // The event being managed, for the screen title.
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

  // Candidate members for the fk-lookup select.
  const membersQuery = useBifrostQuery<Array<Record<string, unknown>>>(
    membersQueryName,
    {
      fields: ['id', 'first_name', 'last_name'],
      enabled: !!eventId,
    },
  );
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

  const fkOptions = useMemo(
    () =>
      members.map((m) => ({
        key: String(m.id),
        label: [m.first_name, m.last_name].filter((v) => v != null).join(' '),
      })),
    [members],
  );

  const insert = useBifrostMutation(buildInsertMutation(rsvpQueryName), {
    invalidateQueries: [rsvpQueryName],
  });
  const update = useBifrostMutation(buildUpdateMutation(rsvpQueryName), {
    invalidateQueries: [rsvpQueryName],
  });

  // Add-form state: a selected member, a response, and a guest count.
  const [newMemberId, setNewMemberId] = useState<unknown>('');
  const [newResponse, setNewResponse] = useState<unknown>('');
  const [newGuests, setNewGuests] = useState<unknown>('');

  // Per-row edit buffers, keyed by RSVP id.
  const [responseEdits, setResponseEdits] = useState<Record<string, string>>(
    {},
  );
  const [guestsEdits, setGuestsEdits] = useState<Record<string, string>>({});

  const responseFor = (rsvp: RsvpRow) =>
    responseEdits[String(rsvp.id)] ?? rsvp.response ?? '';
  const guestsFor = (rsvp: RsvpRow) =>
    guestsEdits[String(rsvp.id)] ?? String(rsvp.guests ?? '');

  const handleAdd = () => {
    if (!canWrite || !eventId || !newMemberId) {
      return;
    }
    insert.mutate({
      detail: {
        event_id: eventId,
        member_id: newMemberId,
        response: newResponse || null,
        guests: newGuests === '' ? null : Number(newGuests),
      },
    });
    setNewMemberId('');
    setNewResponse('');
    setNewGuests('');
  };

  const handleSaveRow = (rsvp: RsvpRow) => {
    if (!canWrite) {
      return;
    }
    const guests = guestsFor(rsvp);
    update.mutate({
      detail: {
        id: rsvp.id,
        response: responseFor(rsvp) || null,
        guests: guests === '' ? null : Number(guests),
      },
    });
  };

  if (metadataLoading) {
    return <p data-testid="event-rsvps-loading">Loading…</p>;
  }

  if (isError) {
    return (
      <p role="alert" data-testid="event-rsvps-error">
        Failed to load app metadata: {error?.message}
      </p>
    );
  }

  if (!rsvpEntity) {
    return (
      <p role="alert" data-testid="event-rsvps-missing">
        The event_rsvps entity is not declared in the app-metadata overlay.
      </p>
    );
  }

  const title = eventRow?.title
    ? `RSVPs — ${String(eventRow.title)}`
    : (rsvpEntity.label ?? 'Event RSVPs');

  return (
    <section data-testid="event-rsvps">
      <h2>{title}</h2>

      <ul data-testid="event-rsvps-list">
        {rsvps.map((rsvp) => (
          <li key={String(rsvp.id)} data-testid={`event-rsvp-${rsvp.id}`}>
            <span>{memberLabel(rsvp.member_id)}</span>
            {canWrite ? (
              <>
                <select
                  data-testid={`event-rsvp-response-${rsvp.id}`}
                  value={responseFor(rsvp)}
                  onChange={(e) =>
                    setResponseEdits((prev) => ({
                      ...prev,
                      [String(rsvp.id)]: e.target.value,
                    }))
                  }
                >
                  <option value="">{'—'}</option>
                  {RESPONSE_OPTIONS.map((option) => (
                    <option key={option} value={option}>
                      {option}
                    </option>
                  ))}
                </select>
                <input
                  type="number"
                  data-testid={`event-rsvp-guests-${rsvp.id}`}
                  value={guestsFor(rsvp)}
                  onChange={(e) =>
                    setGuestsEdits((prev) => ({
                      ...prev,
                      [String(rsvp.id)]: e.target.value,
                    }))
                  }
                />
                <button
                  type="button"
                  data-testid={`event-rsvp-save-${rsvp.id}`}
                  onClick={() => handleSaveRow(rsvp)}
                >
                  Save
                </button>
              </>
            ) : (
              <span data-testid={`event-rsvp-response-text-${rsvp.id}`}>
                {rsvp.response}
              </span>
            )}
          </li>
        ))}
      </ul>

      {canWrite ? (
        <div data-testid="event-rsvps-add">
          <FieldControl
            name="member_id"
            field={rsvpEntity.fields?.member_id}
            label="member_id"
            value={newMemberId}
            fkOptions={fkOptions}
            fkTargetEntity={MEMBERS_ENTITY_KEY}
            onChange={(value) => setNewMemberId(value)}
          />
          <FieldControl
            name="response"
            field={rsvpEntity.fields?.response}
            label="response"
            value={newResponse}
            enumOptions={RESPONSE_OPTIONS}
            onChange={(value) => setNewResponse(value)}
          />
          <FieldControl
            name="guests"
            field={rsvpEntity.fields?.guests}
            label="guests"
            value={newGuests}
            onChange={(value) => setNewGuests(value)}
          />
          <button type="button" onClick={handleAdd}>
            Record RSVP
          </button>
        </div>
      ) : null}

      <div className="event-rsvps__actions">
        <button type="button" onClick={() => navigate('/events')}>
          Back to events
        </button>
      </div>
    </section>
  );
}
