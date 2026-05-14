import { useMemo, useState } from 'react';
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
import { buildFilterControls, buildTableFilter } from './member-list-filters';

/** Qualified entity key of the members entity in the app-metadata overlay. */
const MEMBERS_ENTITY_KEY = 'main.members';

/**
 * Permission required to deactivate a member. Officers and admins hold this;
 * read-only members do not, so the Deactivate row action is hidden for them.
 */
const MEMBERS_WRITE = 'main.members.write';

/** Status value a deactivated member is set to. */
const INACTIVE_STATUS = 'inactive';

/**
 * Metadata-driven member roster screen.
 *
 * Columns and filter controls are derived from the `main.members` entity in the
 * app-metadata overlay via {@link useAppMetadata} — search, the status filter,
 * and any tag/renewal columns appear only because the overlay declares those
 * fields, never from hardcoded names. Data is rendered with the
 * `@bifrostql/react` {@link BifrostTable}. Each row offers a View action that
 * routes to the member detail screen, and — for officers/admins holding
 * `main.members.write` — a Deactivate action wired to an update mutation that
 * sets the member's status to `inactive`.
 */
export function MemberList() {
  const navigate = useNavigate();
  const { entities, isLoading, isError, error } = useAppMetadata();
  const { permissions } = useSession();

  const entity = entities[MEMBERS_ENTITY_KEY];
  const queryName = useMemo(
    () => entityKeyToQueryName(MEMBERS_ENTITY_KEY),
    [],
  );

  const columns = useMemo(
    () => (entity ? buildColumns(entity) : []),
    [entity],
  );
  const filterControls = useMemo(
    () => (entity ? buildFilterControls(entity) : []),
    [entity],
  );

  const [filterValues, setFilterValues] = useState<Record<string, string>>({});
  const tableFilter = useMemo(
    () => buildTableFilter(filterControls, filterValues),
    [filterControls, filterValues],
  );

  const canWrite = permissions.includes(MEMBERS_WRITE);

  const deactivate = useBifrostMutation(buildUpdateMutation(queryName), {
    invalidateQueries: [queryName],
  });

  const rowActions = useMemo<RowAction[]>(() => {
    const actions: RowAction[] = [
      {
        label: 'View',
        onClick: (row) => navigate(`/members/${String(row.id)}`),
      },
    ];
    if (canWrite) {
      actions.push({
        label: 'Deactivate',
        onClick: (row) => {
          deactivate.mutate({
            detail: { id: row.id, status: INACTIVE_STATUS },
          });
        },
      });
    }
    return actions;
  }, [navigate, canWrite, deactivate]);

  if (isLoading) {
    return <p data-testid="member-list-loading">Loading members…</p>;
  }

  if (isError) {
    return (
      <p role="alert" data-testid="member-list-error">
        Failed to load app metadata: {error?.message}
      </p>
    );
  }

  if (!entity) {
    return (
      <p role="alert" data-testid="member-list-missing">
        The members entity is not declared in the app-metadata overlay.
      </p>
    );
  }

  return (
    <section data-testid="member-list">
      <h2>{entity.label ?? 'Members'}</h2>

      <div className="member-list__filters" data-testid="member-list-filters">
        {filterControls.map((control) =>
          control.kind === 'select' ? (
            <label key={control.field}>
              {control.label}
              <select
                data-testid={`filter-${control.field}`}
                value={filterValues[control.field] ?? ''}
                onChange={(e) =>
                  setFilterValues((prev) => ({
                    ...prev,
                    [control.field]: e.target.value,
                  }))
                }
              >
                <option value="">All</option>
                {control.options.map((option) => (
                  <option key={option} value={option}>
                    {option}
                  </option>
                ))}
              </select>
            </label>
          ) : (
            <label key={control.field}>
              {control.label}
              <input
                type="search"
                data-testid={`filter-${control.field}`}
                placeholder={`Search ${control.label}`}
                value={filterValues[control.field] ?? ''}
                onChange={(e) =>
                  setFilterValues((prev) => ({
                    ...prev,
                    [control.field]: e.target.value,
                  }))
                }
              />
            </label>
          ),
        )}
      </div>

      <BifrostTable
        query={queryName}
        columns={columns}
        rowActions={rowActions}
        defaultFilters={tableFilter}
        onRowClick={(row) => navigate(`/members/${String(row.id)}`)}
      />
    </section>
  );
}
