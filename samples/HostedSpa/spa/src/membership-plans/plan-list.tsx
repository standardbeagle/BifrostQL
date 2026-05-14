import { useMemo } from 'react';
import { useNavigate } from '@standardbeagle/virtual-router';
import {
  buildColumns,
  entityKeyToQueryName,
  useAppMetadata,
  useSession,
} from '@bifrostql/app-shell';
import {
  BifrostTable,
  useBifrostMutation,
  buildUpdateMutation,
} from '@bifrostql/react';
import type { RowAction } from '@bifrostql/react';
import { canReadFinanceFields, isFinanceField } from './finance-fields';

/** Qualified entity key of the membership_plans entity in the overlay. */
const PLANS_ENTITY_KEY = 'main.membership_plans';

/**
 * Permission required to create, edit, and deactivate plans. Officers and
 * admins hold this; read-only members do not, so the Deactivate row action is
 * hidden for them.
 */
const MEMBERS_WRITE = 'main.members.write';

/**
 * Metadata-driven membership-plan roster screen.
 *
 * Columns are derived from the `main.membership_plans` entity in the
 * app-metadata overlay via {@link buildColumns} — they appear only because the
 * overlay declares those fields, never from hardcoded names. Data is rendered
 * with the `@bifrostql/react` {@link BifrostTable}. Each row offers a View
 * action routing to the plan detail screen, and — for officers/admins holding
 * `main.members.write` — a Deactivate action wired to an update mutation that
 * sets `is_active` to `false` (a plan is deactivated, never deleted, so
 * existing memberships keep their plan reference).
 *
 * Mirrors the `MemberList` screen structure. Must be mounted within an
 * `AppShellProvider`, inside `AppLayout` / `ProtectedRoute`.
 */
export function PlanList() {
  const navigate = useNavigate();
  const { entities, isLoading, isError, error } = useAppMetadata();
  const { permissions } = useSession();

  const entity = entities[PLANS_ENTITY_KEY];
  const queryName = useMemo(
    () => entityKeyToQueryName(PLANS_ENTITY_KEY),
    [],
  );

  // Columns are overlay-driven; `price_cents` is then dropped for non-finance
  // sessions so they never name a column the host's `policy-read-deny` rejects.
  const canReadFinance = canReadFinanceFields(permissions);
  const columns = useMemo(() => {
    if (!entity) {
      return [];
    }
    const built = buildColumns(entity);
    if (canReadFinance) {
      return built;
    }
    return built.filter(
      (column) => !isFinanceField(column.field, PLANS_ENTITY_KEY),
    );
  }, [entity, canReadFinance]);

  const canWrite = permissions.includes(MEMBERS_WRITE);

  const deactivate = useBifrostMutation(buildUpdateMutation(queryName), {
    invalidateQueries: [queryName],
  });

  const rowActions = useMemo<RowAction[]>(() => {
    const actions: RowAction[] = [
      {
        label: 'View',
        onClick: (row) => navigate(`/plans/${String(row.id)}`),
      },
    ];
    if (canWrite) {
      actions.push({
        label: 'Deactivate',
        onClick: (row) => {
          deactivate.mutate({
            detail: { id: row.id, is_active: false },
          });
        },
      });
    }
    return actions;
  }, [navigate, canWrite, deactivate]);

  if (isLoading) {
    return <p data-testid="plan-list-loading">Loading plans…</p>;
  }

  if (isError) {
    return (
      <p role="alert" data-testid="plan-list-error">
        Failed to load app metadata: {error?.message}
      </p>
    );
  }

  if (!entity) {
    return (
      <p role="alert" data-testid="plan-list-missing">
        The membership_plans entity is not declared in the app-metadata overlay.
      </p>
    );
  }

  return (
    <section data-testid="plan-list">
      <h2>{entity.label ?? 'Membership Plans'}</h2>

      {canWrite ? (
        <div className="plan-list__actions">
          <button type="button" onClick={() => navigate('/plans/new')}>
            New plan
          </button>
        </div>
      ) : null}

      <BifrostTable
        query={queryName}
        columns={columns}
        rowActions={rowActions}
        onRowClick={(row) => navigate(`/plans/${String(row.id)}`)}
      />
    </section>
  );
}
