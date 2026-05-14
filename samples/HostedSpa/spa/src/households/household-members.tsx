import { useMemo, useState } from 'react';
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
  buildDeleteMutation,
} from '@bifrostql/react';

/** Qualified entity key of the household_members link entity in the overlay. */
const HOUSEHOLD_MEMBERS_ENTITY_KEY = 'main.household_members';

/** Qualified entity key of the members entity, the fk-lookup target. */
const MEMBERS_ENTITY_KEY = 'main.members';

/**
 * Permission required to add, edit, and remove household-member links.
 * Read-only sessions still see the link list but get no mutating controls.
 */
const MEMBERS_WRITE = 'main.members.write';

/**
 * Relationship roles for the `relationship` select. The overlay declares the
 * field as a `select` but does not carry an enum option set, so the role
 * vocabulary (per the overlay `helpText`: "head, spouse, child") is supplied
 * here.
 */
const RELATIONSHIP_ROLES = ['head', 'spouse', 'child', 'other'];

/** Shape of a household_members link row as loaded from GraphQL. */
interface LinkRow {
  id: number | string;
  household_id: number | string;
  member_id: number | string;
  relationship: string | null;
}

/** Props for {@link HouseholdMembers}. */
export interface HouseholdMembersProps {
  /** Id of the household whose member links are managed. */
  householdId: number | string | undefined;
}

/**
 * FK-free relationship controls for a household's member links.
 *
 * Lists every `main.household_members` row for the household, resolving each
 * link's `member_id` to the member's display name via the `main.members`
 * entity — the raw FK id is never shown or typed. Adding a link uses an
 * {@link FieldControl} `fk-lookup` `<select>` over candidate members plus a
 * `relationship` role select, so a new link is created entirely by selection.
 * Each existing row offers an inline `relationship` select (Save → update
 * mutation) and a Remove action (delete mutation). All mutating controls are
 * gated on `main.members.write`.
 *
 * Must be mounted within an `AppShellProvider`; intended to sit inside
 * `HouseholdForm` in edit mode.
 */
export function HouseholdMembers({ householdId }: HouseholdMembersProps) {
  const { entities, isLoading: metadataLoading, isError, error } =
    useAppMetadata();
  const { permissions } = useSession();

  const canWrite = permissions.includes(MEMBERS_WRITE);

  const linkEntity = entities[HOUSEHOLD_MEMBERS_ENTITY_KEY];
  const linkQueryName = useMemo(
    () => entityKeyToQueryName(HOUSEHOLD_MEMBERS_ENTITY_KEY),
    [],
  );
  const membersQueryName = useMemo(
    () => entityKeyToQueryName(MEMBERS_ENTITY_KEY),
    [],
  );

  // Existing links for this household.
  const linksQuery = useBifrostQuery<LinkRow[]>(linkQueryName, {
    fields: ['id', 'household_id', 'member_id', 'relationship'],
    filter: householdId ? { household_id: { _eq: householdId } } : undefined,
    enabled: !!householdId,
  });
  const links = linksQuery.data ?? [];

  // Candidate members for the fk-lookup select.
  const membersQuery = useBifrostQuery<
    Array<Record<string, unknown>>
  >(membersQueryName, {
    fields: ['id', 'first_name', 'last_name'],
    enabled: !!householdId,
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

  const fkOptions = useMemo(
    () =>
      members.map((m) => ({
        key: String(m.id),
        label: [m.first_name, m.last_name].filter((v) => v != null).join(' '),
      })),
    [members],
  );

  const insert = useBifrostMutation(buildInsertMutation(linkQueryName), {
    invalidateQueries: [linkQueryName],
  });
  const update = useBifrostMutation(buildUpdateMutation(linkQueryName), {
    invalidateQueries: [linkQueryName],
  });
  const remove = useBifrostMutation(buildDeleteMutation(linkQueryName), {
    invalidateQueries: [linkQueryName],
  });

  // Add-form state: a selected member and a relationship role.
  const [newMemberId, setNewMemberId] = useState<unknown>('');
  const [newRelationship, setNewRelationship] = useState<unknown>('');

  // Per-row relationship edit buffer, keyed by link id.
  const [rowEdits, setRowEdits] = useState<Record<string, string>>({});

  const relationshipFor = (link: LinkRow) =>
    rowEdits[String(link.id)] ?? link.relationship ?? '';

  const handleAdd = () => {
    if (!canWrite || !householdId || !newMemberId) {
      return;
    }
    insert.mutate({
      detail: {
        household_id: householdId,
        member_id: newMemberId,
        relationship: newRelationship || null,
      },
    });
    setNewMemberId('');
    setNewRelationship('');
  };

  const handleSaveRow = (link: LinkRow) => {
    if (!canWrite) {
      return;
    }
    update.mutate({
      detail: { id: link.id, relationship: relationshipFor(link) },
    });
  };

  const handleRemove = (link: LinkRow) => {
    if (!canWrite) {
      return;
    }
    remove.mutate({ detail: { id: link.id } });
  };

  if (metadataLoading) {
    return <p data-testid="household-members-loading">Loading…</p>;
  }

  if (isError) {
    return (
      <p role="alert" data-testid="household-members-error">
        Failed to load app metadata: {error?.message}
      </p>
    );
  }

  if (!linkEntity) {
    return (
      <p role="alert" data-testid="household-members-missing">
        The household_members entity is not declared in the app-metadata
        overlay.
      </p>
    );
  }

  return (
    <section data-testid="household-members">
      <h3>{linkEntity.label ?? 'Household Members'}</h3>

      <ul data-testid="household-members-list">
        {links.map((link) => (
          <li
            key={String(link.id)}
            data-testid={`household-member-${link.id}`}
          >
            <span>{memberLabel(link.member_id)}</span>
            {canWrite ? (
              <>
                <select
                  data-testid={`household-member-relationship-${link.id}`}
                  value={relationshipFor(link)}
                  onChange={(e) =>
                    setRowEdits((prev) => ({
                      ...prev,
                      [String(link.id)]: e.target.value,
                    }))
                  }
                >
                  <option value="">{'—'}</option>
                  {RELATIONSHIP_ROLES.map((role) => (
                    <option key={role} value={role}>
                      {role}
                    </option>
                  ))}
                </select>
                <button
                  type="button"
                  data-testid={`household-member-save-${link.id}`}
                  onClick={() => handleSaveRow(link)}
                >
                  Save
                </button>
                <button
                  type="button"
                  data-testid={`household-member-remove-${link.id}`}
                  onClick={() => handleRemove(link)}
                >
                  Remove
                </button>
              </>
            ) : (
              <span data-testid={`household-member-role-${link.id}`}>
                {link.relationship}
              </span>
            )}
          </li>
        ))}
      </ul>

      {canWrite ? (
        <div data-testid="household-members-add">
          <FieldControl
            name="member_id"
            field={linkEntity.fields?.member_id}
            label="member_id"
            value={newMemberId}
            fkOptions={fkOptions}
            fkTargetEntity={MEMBERS_ENTITY_KEY}
            onChange={(value) => setNewMemberId(value)}
          />
          <FieldControl
            name="relationship"
            field={linkEntity.fields?.relationship}
            label="relationship"
            value={newRelationship}
            enumOptions={RELATIONSHIP_ROLES}
            onChange={(value) => setNewRelationship(value)}
          />
          <button type="button" onClick={handleAdd}>
            Add member
          </button>
        </div>
      ) : null}
    </section>
  );
}
