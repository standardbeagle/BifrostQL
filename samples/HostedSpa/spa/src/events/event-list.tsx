import { useMemo } from 'react';
import { useNavigate } from '@standardbeagle/virtual-router';
import {
  buildColumns,
  entityKeyToQueryName,
  useAppMetadata,
  useSession,
} from '@bifrostql/app-shell';
import { BifrostTable } from '@bifrostql/react';
import type { RowAction } from '@bifrostql/react';

/** Qualified entity key of the events entity in the overlay. */
const EVENTS_ENTITY_KEY = 'main.events';

/**
 * Permission required to create and edit events. No distinct event-manager
 * permission is wired yet, so officers/admins are identified by the existing
 * `main.members.write` permission; read-only members do not hold it, so the
 * New event action and the RSVPs row action's edit affordances are gated the
 * same way the rest of the SPA gates writes.
 */
const MEMBERS_WRITE = 'main.members.write';

/**
 * Metadata-driven event roster screen.
 *
 * Columns are derived from the `main.events` entity in the app-metadata overlay
 * via {@link buildColumns} — they appear only because the overlay declares
 * those fields, never from hardcoded names. Data is rendered with the
 * `@bifrostql/react` {@link BifrostTable}. Each row offers a View action
 * routing to the event detail screen and a Manage RSVPs action routing to the
 * per-event RSVP screen.
 *
 * Mirrors the `PlanList` screen structure. Must be mounted within an
 * `AppShellProvider`, inside `AppLayout` / `ProtectedRoute`.
 */
export function EventList() {
  const navigate = useNavigate();
  const { entities, isLoading, isError, error } = useAppMetadata();
  const { permissions } = useSession();

  const entity = entities[EVENTS_ENTITY_KEY];
  const queryName = useMemo(
    () => entityKeyToQueryName(EVENTS_ENTITY_KEY),
    [],
  );

  const columns = useMemo(
    () => (entity ? buildColumns(entity) : []),
    [entity],
  );

  const canWrite = permissions.includes(MEMBERS_WRITE);

  const rowActions = useMemo<RowAction[]>(
    () => [
      {
        label: 'View',
        onClick: (row) => navigate(`/events/${String(row.id)}`),
      },
      {
        label: 'Manage RSVPs',
        onClick: (row) => navigate(`/events/${String(row.id)}/rsvps`),
      },
    ],
    [navigate],
  );

  if (isLoading) {
    return <p data-testid="event-list-loading">Loading events…</p>;
  }

  if (isError) {
    return (
      <p role="alert" data-testid="event-list-error">
        Failed to load app metadata: {error?.message}
      </p>
    );
  }

  if (!entity) {
    return (
      <p role="alert" data-testid="event-list-missing">
        The events entity is not declared in the app-metadata overlay.
      </p>
    );
  }

  return (
    <section data-testid="event-list">
      <h2>{entity.label ?? 'Events'}</h2>

      {canWrite ? (
        <div className="event-list__actions">
          <button type="button" onClick={() => navigate('/events/new')}>
            New event
          </button>
        </div>
      ) : null}

      <BifrostTable
        query={queryName}
        columns={columns}
        rowActions={rowActions}
        onRowClick={(row) => navigate(`/events/${String(row.id)}`)}
      />
    </section>
  );
}
