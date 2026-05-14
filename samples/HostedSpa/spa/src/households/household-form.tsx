import { useMemo, useState } from 'react';
import { useNavigate, useParams } from '@standardbeagle/virtual-router';
import {
  FieldControl,
  entityKeyToQueryName,
  useAppMetadata,
  useSession,
} from '@bifrostql/app-shell';
import type { EntityMetadata, FieldMetadata } from '@bifrostql/app-shell';
import {
  useBifrostQuery,
  useBifrostMutation,
  buildInsertMutation,
  buildUpdateMutation,
} from '@bifrostql/react';
import { HouseholdMembers } from './household-members';

/** Qualified entity key of the households entity in the app-metadata overlay. */
const HOUSEHOLDS_ENTITY_KEY = 'main.households';

/** Qualified entity key of the members entity (used for the linked-member list). */
const MEMBERS_ENTITY_KEY = 'main.members';

/**
 * Permission required to create and edit households. Officers and admins hold
 * it; read-only members do not, so for them the form renders non-editable with
 * no Save action.
 */
const MEMBERS_WRITE = 'main.members.write';

/** Permission that grants visibility of admin-only (`visible: false`) fields. */
const MEMBERS_ADMIN = 'main.members.admin';

/** Route param value that selects create mode (`/households/new`). */
const CREATE_PARAM = 'new';

/** A metadata-driven field descriptor for the household form. */
interface HouseholdFormField {
  /** Field name (overlay key, used as the GraphQL column name). */
  name: string;
  /** Field-level overlay metadata driving widget selection. */
  field: FieldMetadata;
  /** `true` when the field is editable-blocked (overlay `readOnly`). */
  readOnly: boolean;
}

/**
 * Derive the ordered household-form fields from entity metadata, gated by the
 * session's permissions. Mirrors `buildMemberFormFields`: order prefers the
 * entity's `displayFields` then the remaining `fields` in declaration order,
 * and `visible: false` fields are admin-only — omitted unless the session
 * holds `main.members.admin`.
 */
function buildHouseholdFormFields(
  entity: EntityMetadata,
  permissions: string[],
): HouseholdFormField[] {
  const fields: Record<string, FieldMetadata> = entity.fields ?? {};
  const isAdmin = permissions.includes(MEMBERS_ADMIN);
  const ordered = [
    ...(entity.displayFields ?? []),
    ...Object.keys(fields).filter(
      (name) => !(entity.displayFields ?? []).includes(name),
    ),
  ];
  return ordered
    .map((name): HouseholdFormField | null => {
      const field = fields[name];
      if (!field) {
        return null;
      }
      if (field.visible === false && !isAdmin) {
        return null;
      }
      return { name, field, readOnly: field.readOnly === true };
    })
    .filter((entry): entry is HouseholdFormField => entry !== null);
}

/**
 * Metadata-driven household create / edit / detail form.
 *
 * Mode is derived from the `:id` route param: `new` → create, any other value
 * → edit (or read-only detail when the session lacks `main.members.write`).
 * The shared address fields — `name`, `address_line1`, `city`, … — and their
 * order come entirely from the `main.households` overlay via
 * {@link buildHouseholdFormFields}; each renders through {@link FieldControl}
 * so widgets are picked from metadata, never hardcoded. Saving routes through
 * an insert mutation (create) or an update mutation carrying the row `id`
 * (edit).
 *
 * In edit mode the household's linked members are shown two ways: a read-only
 * roster derived from the `members` childCollection relationship, and the
 * {@link HouseholdMembers} relationship editor for FK-free link management.
 *
 * Must be mounted within an `AppShellProvider`, inside `AppLayout` /
 * `ProtectedRoute`.
 */
export function HouseholdForm() {
  const navigate = useNavigate();
  const params = useParams();
  const { entities, isLoading: metadataLoading, isError, error } =
    useAppMetadata();
  const { permissions } = useSession();

  const rawId = params.id;
  const isCreate = rawId === CREATE_PARAM || rawId === undefined;
  const householdId = isCreate ? undefined : rawId;

  const entity = entities[HOUSEHOLDS_ENTITY_KEY];
  const queryName = useMemo(
    () => entityKeyToQueryName(HOUSEHOLDS_ENTITY_KEY),
    [],
  );
  const membersQueryName = useMemo(
    () => entityKeyToQueryName(MEMBERS_ENTITY_KEY),
    [],
  );

  const canWrite = permissions.includes(MEMBERS_WRITE);

  const formFields = useMemo(
    () => (entity ? buildHouseholdFormFields(entity, permissions) : []),
    [entity, permissions],
  );

  // Load the existing household in edit mode.
  const householdQuery = useBifrostQuery<Array<Record<string, unknown>>>(
    queryName,
    {
      fields: ['id', ...formFields.map((f) => f.name)],
      filter: householdId ? { id: { _eq: householdId } } : undefined,
      pagination: { limit: 1 },
      enabled: !isCreate && formFields.length > 0,
    },
  );
  const loadedRow = householdQuery.data?.[0];

  // The household's linked members, via the `members` childCollection
  // relationship's foreignKeyField. Read-only roster — link management lives in
  // HouseholdMembers.
  const membersRelationship = entity?.relationships?.members;
  const linkedFk = membersRelationship?.foreignKeyField ?? 'household_id';
  const linkedColumns = membersRelationship?.displayColumns ?? [
    'first_name',
    'last_name',
  ];
  const linkedMembersQuery = useBifrostQuery<Array<Record<string, unknown>>>(
    membersQueryName,
    {
      fields: ['id', ...linkedColumns],
      filter: householdId ? { [linkedFk]: { _eq: householdId } } : undefined,
      enabled: !isCreate && !!membersRelationship,
    },
  );
  const linkedMembers = linkedMembersQuery.data ?? [];

  // Local edit buffer, seeded once from the loaded row.
  const [values, setValues] = useState<Record<string, unknown>>({});
  const [seededFor, setSeededFor] = useState<string | null>(null);
  if (!isCreate && loadedRow && seededFor !== householdId) {
    setValues({ ...loadedRow });
    setSeededFor(householdId ?? null);
  }

  const insert = useBifrostMutation(buildInsertMutation(queryName), {
    invalidateQueries: [queryName],
    onSuccess: () => navigate('/'),
  });
  const update = useBifrostMutation(buildUpdateMutation(queryName), {
    invalidateQueries: [queryName],
    onSuccess: () => navigate('/'),
  });

  const setField = (name: string, value: unknown) => {
    setValues((prev) => ({ ...prev, [name]: value }));
  };

  const handleSubmit = (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (!canWrite) {
      return;
    }
    const editable: Record<string, unknown> = {};
    for (const field of formFields) {
      if (!field.readOnly) {
        editable[field.name] = values[field.name];
      }
    }
    if (isCreate) {
      insert.mutate({ detail: editable });
    } else {
      update.mutate({ detail: { id: loadedRow?.id ?? householdId, ...editable } });
    }
  };

  if (metadataLoading) {
    return <p data-testid="household-form-loading">Loading…</p>;
  }

  if (isError) {
    return (
      <p role="alert" data-testid="household-form-error">
        Failed to load app metadata: {error?.message}
      </p>
    );
  }

  if (!entity) {
    return (
      <p role="alert" data-testid="household-form-missing">
        The households entity is not declared in the app-metadata overlay.
      </p>
    );
  }

  if (!isCreate && householdQuery.isLoading) {
    return <p data-testid="household-form-loading">Loading household…</p>;
  }

  if (!isCreate && !loadedRow) {
    return (
      <p role="alert" data-testid="household-form-not-found">
        Household not found.
      </p>
    );
  }

  const title = isCreate
    ? `New ${entity.label ?? 'Household'}`
    : `Edit ${entity.label ?? 'Household'}`;

  return (
    <section data-testid="household-form-screen">
      <h2>{title}</h2>
      <form data-testid="household-form" onSubmit={handleSubmit}>
        {formFields.map((field) => (
          <div key={field.name} data-testid={`household-field-${field.name}`}>
            <FieldControl
              name={field.name}
              field={field.field}
              label={field.name}
              value={values[field.name]}
              onChange={(value) => setField(field.name, value)}
            />
          </div>
        ))}

        <div className="household-form__actions">
          {canWrite ? (
            <button type="submit">{isCreate ? 'Create' : 'Save'}</button>
          ) : null}
          <button type="button" onClick={() => navigate('/')}>
            Cancel
          </button>
        </div>
      </form>

      {!isCreate ? (
        <>
          <section data-testid="household-linked-members">
            <h3>Linked members</h3>
            {linkedMembers.length === 0 ? (
              <p>No members linked to this household.</p>
            ) : (
              <ul>
                {linkedMembers.map((member) => (
                  <li key={String(member.id)}>
                    {linkedColumns
                      .map((col) => member[col])
                      .filter((v) => v != null)
                      .join(' ')}
                  </li>
                ))}
              </ul>
            )}
          </section>
          <HouseholdMembers householdId={householdId} />
        </>
      ) : null}
    </section>
  );
}
