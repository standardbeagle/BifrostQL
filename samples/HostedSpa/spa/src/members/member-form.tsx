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
import { buildMemberFormFields } from './member-form-fields';

/** Qualified entity key of the members entity in the app-metadata overlay. */
const MEMBERS_ENTITY_KEY = 'main.members';

/**
 * Permission required to create, edit, and deactivate members. Officers and
 * admins hold this; read-only members do not, so for them the form renders in
 * a non-editable detail mode with no Save/Deactivate actions.
 */
const MEMBERS_WRITE = 'main.members.write';

/** Status value a deactivated member is set to. */
const INACTIVE_STATUS = 'inactive';

/** Route param value that selects create mode (`/members/new`). */
const CREATE_PARAM = 'new';

/**
 * Metadata-driven member create / edit / detail form.
 *
 * Mode is derived from the `:id` route param: `new` → create, any other value
 * → edit (or read-only detail when the session lacks `main.members.write`). In
 * edit mode the member row is loaded with {@link useBifrostQuery} filtered by
 * id. Fields — and crucially their *visibility* — come entirely from the
 * `main.members` overlay via {@link buildMemberFormFields}: `visible: false`
 * fields (`tenant_id`, `deleted_at`, …) are the admin-only/audit fields and are
 * shown only to sessions holding `main.members.admin`, always read-only. Each
 * field renders through {@link FieldControl} so scalar/date/select/fk widgets
 * are picked from metadata, never hardcoded.
 *
 * Saving routes through {@link useBifrostMutation}: an insert mutation in
 * create mode, an update mutation (carrying the row `id`) in edit mode. The
 * Deactivate action — available to writers in edit mode, mirroring the list
 * screen's row action — is an update mutation setting `status` to `inactive`.
 *
 * Must be mounted within an `AppShellProvider` and is intended to sit inside
 * `AppLayout` / `ProtectedRoute`.
 */
export function MemberForm() {
  const navigate = useNavigate();
  const params = useParams();
  const { entities, isLoading: metadataLoading, isError, error } =
    useAppMetadata();
  const { permissions } = useSession();

  const rawId = params.id;
  const isCreate = rawId === CREATE_PARAM || rawId === undefined;
  const memberId = isCreate ? undefined : rawId;

  const entity = entities[MEMBERS_ENTITY_KEY];
  const queryName = useMemo(
    () => entityKeyToQueryName(MEMBERS_ENTITY_KEY),
    [],
  );

  const canWrite = permissions.includes(MEMBERS_WRITE);

  const formFields = useMemo(
    () => (entity ? buildMemberFormFields(entity, permissions) : []),
    [entity, permissions],
  );

  // Load the existing member in edit mode. The filter keys the row by id; the
  // selected fields are the overlay-driven form fields plus id.
  const memberQuery = useBifrostQuery<Array<Record<string, unknown>>>(
    queryName,
    {
      fields: ['id', ...formFields.map((f) => f.name)],
      filter: memberId ? { id: { _eq: memberId } } : undefined,
      pagination: { limit: 1 },
      enabled: !isCreate && formFields.length > 0,
    },
  );
  const loadedRow = memberQuery.data?.[0];

  // Local edit buffer. Seeded from the loaded row once it arrives; tracked with
  // a sentinel so a late-arriving row hydrates the form exactly once.
  const [values, setValues] = useState<Record<string, unknown>>({});
  const [seededFor, setSeededFor] = useState<string | null>(null);
  if (!isCreate && loadedRow && seededFor !== memberId) {
    setValues({ ...loadedRow });
    setSeededFor(memberId ?? null);
  }

  const insert = useBifrostMutation(buildInsertMutation(queryName), {
    invalidateQueries: [queryName],
    onSuccess: () => navigate('/members'),
  });
  const update = useBifrostMutation(buildUpdateMutation(queryName), {
    invalidateQueries: [queryName],
    onSuccess: () => navigate('/members'),
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
      update.mutate({ detail: { id: loadedRow?.id ?? memberId, ...editable } });
    }
  };

  const handleDeactivate = () => {
    if (!canWrite || isCreate) {
      return;
    }
    update.mutate({
      detail: { id: loadedRow?.id ?? memberId, status: INACTIVE_STATUS },
    });
  };

  if (metadataLoading) {
    return <p data-testid="member-form-loading">Loading…</p>;
  }

  if (isError) {
    return (
      <p role="alert" data-testid="member-form-error">
        Failed to load app metadata: {error?.message}
      </p>
    );
  }

  if (!entity) {
    return (
      <p role="alert" data-testid="member-form-missing">
        The members entity is not declared in the app-metadata overlay.
      </p>
    );
  }

  if (!isCreate && memberQuery.isLoading) {
    return <p data-testid="member-form-loading">Loading member…</p>;
  }

  if (!isCreate && !loadedRow) {
    return (
      <p role="alert" data-testid="member-form-not-found">
        Member not found.
      </p>
    );
  }

  const title = isCreate
    ? `New ${entity.label ?? 'Member'}`
    : `Edit ${entity.label ?? 'Member'}`;

  return (
    <section data-testid="member-form-screen">
      <h2>{title}</h2>
      <form data-testid="member-form" onSubmit={handleSubmit}>
        {formFields.map((field) => (
          <div key={field.name} data-testid={`member-field-${field.name}`}>
            <FieldControl
              name={field.name}
              field={field.field}
              label={field.name}
              value={values[field.name]}
              onChange={(value) => setField(field.name, value)}
            />
            {field.adminOnly ? (
              <span
                className="member-form__admin-badge"
                data-testid={`member-field-${field.name}-admin`}
              >
                Admin-only
              </span>
            ) : null}
          </div>
        ))}

        <div className="member-form__actions">
          {canWrite ? (
            <button type="submit">{isCreate ? 'Create' : 'Save'}</button>
          ) : null}
          {canWrite && !isCreate ? (
            <button type="button" onClick={handleDeactivate}>
              Deactivate
            </button>
          ) : null}
          <button type="button" onClick={() => navigate('/members')}>
            Cancel
          </button>
        </div>
      </form>
    </section>
  );
}
