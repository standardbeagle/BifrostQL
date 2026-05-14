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
} from '@bifrostql/react';

/** Qualified entity key of the member_memberships link entity in the overlay. */
const MEMBER_MEMBERSHIPS_ENTITY_KEY = 'main.member_memberships';

/** Qualified entity key of the members entity, an fk-lookup target. */
const MEMBERS_ENTITY_KEY = 'main.members';

/** Qualified entity key of the membership_plans entity, an fk-lookup target. */
const PLANS_ENTITY_KEY = 'main.membership_plans';

/**
 * Permission required to assign a member to a plan. Read-only sessions still
 * see the assignment list but get no assigning controls.
 */
const MEMBERS_WRITE = 'main.members.write';

/**
 * Status vocabulary for the membership `status` select. The overlay declares
 * the field as a `select` but carries no enum option set (the same pattern as
 * `household_members.relationship`), so the vocabulary is supplied here. New
 * assignments default to `active`.
 */
const MEMBERSHIP_STATUSES = ['active', 'lapsed', 'cancelled'];

/** Shape of a member_memberships row as loaded from GraphQL. */
interface MembershipRow {
  id: number | string;
  member_id: number | string;
  plan_id: number | string;
  start_date: string | null;
  end_date: string | null;
  status: string | null;
}

/**
 * Member-to-plan assignment screen.
 *
 * Lists every `main.member_memberships` row, resolving each `member_id` and
 * `plan_id` to display names via the `main.members` / `main.membership_plans`
 * entities — raw FK ids are never shown or typed. Creating an assignment uses
 * {@link FieldControl} `fk-lookup` `<select>`s over candidate members and
 * active plans, plus a `start_date` datepicker (the initial renewal date) and a
 * `status` select, so a new membership is created entirely by selection.
 * Writing routes through an insert mutation on `member_memberships`. All
 * assigning controls are gated on `main.members.write`.
 *
 * Mirrors the `HouseholdMembers` FK-free relationship-editor pattern. Must be
 * mounted within an `AppShellProvider`, inside `AppLayout` / `ProtectedRoute`.
 */
export function MemberPlanAssignment() {
  const { entities, isLoading: metadataLoading, isError, error } =
    useAppMetadata();
  const { permissions } = useSession();

  const canWrite = permissions.includes(MEMBERS_WRITE);

  const membershipEntity = entities[MEMBER_MEMBERSHIPS_ENTITY_KEY];
  const membershipQueryName = useMemo(
    () => entityKeyToQueryName(MEMBER_MEMBERSHIPS_ENTITY_KEY),
    [],
  );
  const membersQueryName = useMemo(
    () => entityKeyToQueryName(MEMBERS_ENTITY_KEY),
    [],
  );
  const plansQueryName = useMemo(
    () => entityKeyToQueryName(PLANS_ENTITY_KEY),
    [],
  );

  // Existing assignments.
  const membershipsQuery = useBifrostQuery<MembershipRow[]>(
    membershipQueryName,
    {
      fields: [
        'id',
        'member_id',
        'plan_id',
        'start_date',
        'end_date',
        'status',
      ],
    },
  );
  const memberships = membershipsQuery.data ?? [];

  // Candidate members for the fk-lookup select.
  const membersQuery = useBifrostQuery<Array<Record<string, unknown>>>(
    membersQueryName,
    { fields: ['id', 'first_name', 'last_name'] },
  );
  const members = useMemo(
    () => membersQuery.data ?? [],
    [membersQuery.data],
  );

  // Candidate plans for the fk-lookup select. Only active plans can be
  // assigned, mirroring the overlay's `is_active = 1` default grid filter.
  const plansQuery = useBifrostQuery<Array<Record<string, unknown>>>(
    plansQueryName,
    { fields: ['id', 'name', 'is_active'], filter: { is_active: { _eq: true } } },
  );
  const plans = useMemo(() => plansQuery.data ?? [], [plansQuery.data]);

  const memberLabel = (memberId: number | string | null | undefined) => {
    const member = members.find((m) => String(m.id) === String(memberId));
    if (!member) {
      return String(memberId ?? '');
    }
    return [member.first_name, member.last_name]
      .filter((v) => v != null)
      .join(' ');
  };

  const planLabel = (planId: number | string | null | undefined) => {
    const plan = plans.find((p) => String(p.id) === String(planId));
    return plan ? String(plan.name) : String(planId ?? '');
  };

  const memberOptions = useMemo(
    () =>
      members.map((m) => ({
        key: String(m.id),
        label: [m.first_name, m.last_name].filter((v) => v != null).join(' '),
      })),
    [members],
  );
  const planOptions = useMemo(
    () => plans.map((p) => ({ key: String(p.id), label: String(p.name) })),
    [plans],
  );

  const insert = useBifrostMutation(
    buildInsertMutation(membershipQueryName),
    { invalidateQueries: [membershipQueryName] },
  );

  // Assign-form state.
  const [newMemberId, setNewMemberId] = useState<unknown>('');
  const [newPlanId, setNewPlanId] = useState<unknown>('');
  const [newStartDate, setNewStartDate] = useState<unknown>('');
  const [newStatus, setNewStatus] = useState<unknown>('active');

  const handleAssign = () => {
    if (!canWrite || !newMemberId || !newPlanId) {
      return;
    }
    insert.mutate({
      detail: {
        member_id: newMemberId,
        plan_id: newPlanId,
        start_date: newStartDate || null,
        status: newStatus || null,
      },
    });
    setNewMemberId('');
    setNewPlanId('');
    setNewStartDate('');
    setNewStatus('active');
  };

  if (metadataLoading) {
    return <p data-testid="member-plan-assignment-loading">Loading…</p>;
  }

  if (isError) {
    return (
      <p role="alert" data-testid="member-plan-assignment-error">
        Failed to load app metadata: {error?.message}
      </p>
    );
  }

  if (!membershipEntity) {
    return (
      <p role="alert" data-testid="member-plan-assignment-missing">
        The member_memberships entity is not declared in the app-metadata
        overlay.
      </p>
    );
  }

  return (
    <section data-testid="member-plan-assignment">
      <h2>{membershipEntity.label ?? 'Member Memberships'}</h2>

      <ul data-testid="member-plan-assignment-list">
        {memberships.map((membership) => (
          <li
            key={String(membership.id)}
            data-testid={`member-plan-assignment-${membership.id}`}
          >
            <span>{memberLabel(membership.member_id)}</span>
            {' — '}
            <span>{planLabel(membership.plan_id)}</span>
            {' — '}
            <span>{membership.start_date}</span>
            {' — '}
            <span>{membership.status}</span>
          </li>
        ))}
      </ul>

      {canWrite ? (
        <div data-testid="member-plan-assignment-add">
          <FieldControl
            name="member_id"
            field={membershipEntity.fields?.member_id}
            label="member_id"
            value={newMemberId}
            fkOptions={memberOptions}
            fkTargetEntity={MEMBERS_ENTITY_KEY}
            onChange={(value) => setNewMemberId(value)}
          />
          <FieldControl
            name="plan_id"
            field={membershipEntity.fields?.plan_id}
            label="plan_id"
            value={newPlanId}
            fkOptions={planOptions}
            fkTargetEntity={PLANS_ENTITY_KEY}
            onChange={(value) => setNewPlanId(value)}
          />
          <FieldControl
            name="start_date"
            field={membershipEntity.fields?.start_date}
            label="start_date"
            value={newStartDate}
            onChange={(value) => setNewStartDate(value)}
          />
          <FieldControl
            name="status"
            field={membershipEntity.fields?.status}
            label="status"
            value={newStatus}
            enumOptions={MEMBERSHIP_STATUSES}
            onChange={(value) => setNewStatus(value)}
          />
          <button type="button" onClick={handleAssign}>
            Assign plan
          </button>
        </div>
      ) : null}
    </section>
  );
}
