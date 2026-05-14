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
import { buildPlanFormFields } from './plan-form-fields';

/** Qualified entity key of the membership_plans entity in the overlay. */
const PLANS_ENTITY_KEY = 'main.membership_plans';

/**
 * Permission required to create, edit, and deactivate plans. Officers and
 * admins hold this; read-only members do not, so for them the form renders in
 * a non-editable detail mode with no Save/Deactivate actions.
 */
const MEMBERS_WRITE = 'main.members.write';

/** Route param value that selects create mode (`/plans/new`). */
const CREATE_PARAM = 'new';

/**
 * Metadata-driven membership-plan create / edit / detail form.
 *
 * Mode is derived from the `:id` route param: `new` → create, any other value
 * → edit (or read-only detail when the session lacks `main.members.write`). In
 * edit mode the plan row is loaded with {@link useBifrostQuery} filtered by id.
 * Fields — and their visibility — come entirely from the
 * `main.membership_plans` overlay via {@link buildPlanFormFields}: `tenant_id`
 * (`visible: false`) is admin-only and shown read-only only to sessions holding
 * `main.members.admin`. Each field renders through {@link FieldControl} so
 * scalar/textarea/number/checkbox/select widgets are picked from metadata.
 *
 * Saving routes through {@link useBifrostMutation}: an insert mutation in
 * create mode, an update mutation (carrying the row `id`) in edit mode. The
 * Deactivate action — available to writers in edit mode, mirroring the list
 * screen's row action — is an update mutation setting `is_active` to `false`.
 *
 * Mirrors the `MemberForm` screen. Must be mounted within an
 * `AppShellProvider`, inside `AppLayout` / `ProtectedRoute`.
 */
export function PlanForm() {
  const navigate = useNavigate();
  const params = useParams();
  const { entities, isLoading: metadataLoading, isError, error } =
    useAppMetadata();
  const { permissions } = useSession();

  const rawId = params.id;
  const isCreate = rawId === CREATE_PARAM || rawId === undefined;
  const planId = isCreate ? undefined : rawId;

  const entity = entities[PLANS_ENTITY_KEY];
  const queryName = useMemo(
    () => entityKeyToQueryName(PLANS_ENTITY_KEY),
    [],
  );

  const canWrite = permissions.includes(MEMBERS_WRITE);

  const formFields = useMemo(
    () => (entity ? buildPlanFormFields(entity, permissions) : []),
    [entity, permissions],
  );

  // Load the existing plan in edit mode.
  const planQuery = useBifrostQuery<Array<Record<string, unknown>>>(
    queryName,
    {
      fields: ['id', ...formFields.map((f) => f.name)],
      filter: planId ? { id: { _eq: planId } } : undefined,
      pagination: { limit: 1 },
      enabled: !isCreate && formFields.length > 0,
    },
  );
  const loadedRow = planQuery.data?.[0];

  // Local edit buffer, seeded once from the loaded row.
  const [values, setValues] = useState<Record<string, unknown>>({});
  const [seededFor, setSeededFor] = useState<string | null>(null);
  if (!isCreate && loadedRow && seededFor !== planId) {
    setValues({ ...loadedRow });
    setSeededFor(planId ?? null);
  }

  const insert = useBifrostMutation(buildInsertMutation(queryName), {
    invalidateQueries: [queryName],
    onSuccess: () => navigate('/plans'),
  });
  const update = useBifrostMutation(buildUpdateMutation(queryName), {
    invalidateQueries: [queryName],
    onSuccess: () => navigate('/plans'),
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
      update.mutate({ detail: { id: loadedRow?.id ?? planId, ...editable } });
    }
  };

  const handleDeactivate = () => {
    if (!canWrite || isCreate) {
      return;
    }
    update.mutate({
      detail: { id: loadedRow?.id ?? planId, is_active: false },
    });
  };

  if (metadataLoading) {
    return <p data-testid="plan-form-loading">Loading…</p>;
  }

  if (isError) {
    return (
      <p role="alert" data-testid="plan-form-error">
        Failed to load app metadata: {error?.message}
      </p>
    );
  }

  if (!entity) {
    return (
      <p role="alert" data-testid="plan-form-missing">
        The membership_plans entity is not declared in the app-metadata overlay.
      </p>
    );
  }

  if (!isCreate && planQuery.isLoading) {
    return <p data-testid="plan-form-loading">Loading plan…</p>;
  }

  if (!isCreate && !loadedRow) {
    return (
      <p role="alert" data-testid="plan-form-not-found">
        Plan not found.
      </p>
    );
  }

  const title = isCreate
    ? `New ${entity.label ?? 'Plan'}`
    : `Edit ${entity.label ?? 'Plan'}`;

  return (
    <section data-testid="plan-form-screen">
      <h2>{title}</h2>
      <form data-testid="plan-form" onSubmit={handleSubmit}>
        {formFields.map((field) => (
          <div key={field.name} data-testid={`plan-field-${field.name}`}>
            <FieldControl
              name={field.name}
              field={field.field}
              label={field.name}
              value={values[field.name]}
              onChange={(value) => setField(field.name, value)}
            />
            {field.adminOnly ? (
              <span
                className="plan-form__admin-badge"
                data-testid={`plan-field-${field.name}-admin`}
              >
                Admin-only
              </span>
            ) : null}
          </div>
        ))}

        <div className="plan-form__actions">
          {canWrite ? (
            <button type="submit">{isCreate ? 'Create' : 'Save'}</button>
          ) : null}
          {canWrite && !isCreate ? (
            <button type="button" onClick={handleDeactivate}>
              Deactivate
            </button>
          ) : null}
          <button type="button" onClick={() => navigate('/plans')}>
            Cancel
          </button>
        </div>
      </form>
    </section>
  );
}
