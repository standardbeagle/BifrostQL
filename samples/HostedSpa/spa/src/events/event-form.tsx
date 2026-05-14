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
import { buildEventFormFields } from './event-form-fields';

/** Qualified entity key of the events entity in the overlay. */
const EVENTS_ENTITY_KEY = 'main.events';

/**
 * Permission required to create and edit events. No distinct event-manager
 * permission is wired yet, so officers/admins are identified by the existing
 * `main.members.write` permission; read-only members do not hold it, so for
 * them the form renders in a non-editable detail mode with no Save action.
 */
const MEMBERS_WRITE = 'main.members.write';

/** Route param value that selects create mode (`/events/new`). */
const CREATE_PARAM = 'new';

/**
 * Metadata-driven event create / edit / detail form.
 *
 * Mode is derived from the `:id` route param: `new` → create, any other value
 * → edit (or read-only detail when the session lacks `main.members.write`). In
 * edit mode the event row is loaded with {@link useBifrostQuery} filtered by
 * id. Fields — and their visibility — come entirely from the `main.events`
 * overlay via {@link buildEventFormFields}: `created_at` and `tenant_id`
 * (`visible: false`) are admin-only and shown read-only only to sessions
 * holding `main.members.admin`. Each field renders through {@link FieldControl}
 * so text/textarea/datepicker/number widgets are picked from metadata.
 *
 * Saving routes through {@link useBifrostMutation}: an insert mutation in
 * create mode, an update mutation (carrying the row `id`) in edit mode.
 *
 * Mirrors the `PlanForm` screen. Must be mounted within an `AppShellProvider`,
 * inside `AppLayout` / `ProtectedRoute`.
 */
export function EventForm() {
  const navigate = useNavigate();
  const params = useParams();
  const { entities, isLoading: metadataLoading, isError, error } =
    useAppMetadata();
  const { permissions } = useSession();

  const rawId = params.id;
  const isCreate = rawId === CREATE_PARAM || rawId === undefined;
  const eventId = isCreate ? undefined : rawId;

  const entity = entities[EVENTS_ENTITY_KEY];
  const queryName = useMemo(
    () => entityKeyToQueryName(EVENTS_ENTITY_KEY),
    [],
  );

  const canWrite = permissions.includes(MEMBERS_WRITE);

  const formFields = useMemo(
    () => (entity ? buildEventFormFields(entity, permissions) : []),
    [entity, permissions],
  );

  // Load the existing event in edit mode.
  const eventQuery = useBifrostQuery<Array<Record<string, unknown>>>(
    queryName,
    {
      fields: ['id', ...formFields.map((f) => f.name)],
      filter: eventId ? { id: { _eq: eventId } } : undefined,
      pagination: { limit: 1 },
      enabled: !isCreate && formFields.length > 0,
    },
  );
  const loadedRow = eventQuery.data?.[0];

  // Local edit buffer, seeded once from the loaded row.
  const [values, setValues] = useState<Record<string, unknown>>({});
  const [seededFor, setSeededFor] = useState<string | null>(null);
  if (!isCreate && loadedRow && seededFor !== eventId) {
    setValues({ ...loadedRow });
    setSeededFor(eventId ?? null);
  }

  const insert = useBifrostMutation(buildInsertMutation(queryName), {
    invalidateQueries: [queryName],
    onSuccess: () => navigate('/events'),
  });
  const update = useBifrostMutation(buildUpdateMutation(queryName), {
    invalidateQueries: [queryName],
    onSuccess: () => navigate('/events'),
  });

  const setField = (name: string, value: unknown) => {
    setValues((prev) => ({ ...prev, [name]: value }));
  };

  const handleSubmit = (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (!canWrite) {
      return;
    }
    // Only send editable fields; admin-only/read-only fields are display-only.
    const editable: Record<string, unknown> = {};
    for (const field of formFields) {
      if (!field.readOnly) {
        editable[field.name] = values[field.name];
      }
    }
    if (isCreate) {
      insert.mutate({ detail: editable });
    } else {
      update.mutate({ detail: { id: loadedRow?.id ?? eventId, ...editable } });
    }
  };

  if (metadataLoading) {
    return <p data-testid="event-form-loading">Loading…</p>;
  }

  if (isError) {
    return (
      <p role="alert" data-testid="event-form-error">
        Failed to load app metadata: {error?.message}
      </p>
    );
  }

  if (!entity) {
    return (
      <p role="alert" data-testid="event-form-missing">
        The events entity is not declared in the app-metadata overlay.
      </p>
    );
  }

  if (!isCreate && eventQuery.isLoading) {
    return <p data-testid="event-form-loading">Loading event…</p>;
  }

  if (!isCreate && !loadedRow) {
    return (
      <p role="alert" data-testid="event-form-not-found">
        Event not found.
      </p>
    );
  }

  const title = isCreate
    ? `New ${entity.label ?? 'Event'}`
    : `Edit ${entity.label ?? 'Event'}`;

  return (
    <section data-testid="event-form-screen">
      <h2>{title}</h2>
      <form data-testid="event-form" onSubmit={handleSubmit}>
        {formFields.map((field) => (
          <div key={field.name} data-testid={`event-field-${field.name}`}>
            <FieldControl
              name={field.name}
              field={field.field}
              label={field.name}
              value={values[field.name]}
              onChange={(value) => setField(field.name, value)}
            />
            {field.adminOnly ? (
              <span
                className="event-form__admin-badge"
                data-testid={`event-field-${field.name}-admin`}
              >
                Admin-only
              </span>
            ) : null}
          </div>
        ))}

        <div className="event-form__actions">
          {canWrite ? (
            <button type="submit">{isCreate ? 'Create' : 'Save'}</button>
          ) : null}
          <button type="button" onClick={() => navigate('/events')}>
            Cancel
          </button>
        </div>
      </form>
    </section>
  );
}
