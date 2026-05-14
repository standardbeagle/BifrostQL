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
import { getSavedViewOptions } from './saved-views';

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
 * `@bifrostql/react` {@link BifrostTable}. A saved-view picker — also driven by
 * the overlay's `grid.savedViews`, never hardcoded — lets an officer apply a
 * named view's filters to the table. Each row offers a View action that routes
 * to the member detail screen, and — for officers/admins holding
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

  const savedViewOptions = useMemo(
    () => getSavedViewOptions(entity),
    [entity],
  );

  const [filterValues, setFilterValues] = useState<Record<string, string>>({});
  const [savedViewId, setSavedViewId] = useState('');

  const tableFilter = useMemo(() => {
    const fromControls = buildTableFilter(filterControls, filterValues);
    const savedView = savedViewOptions.find((v) => v.id === savedViewId);
    // The selected saved view's filter layers over the ad-hoc filter controls.
    return savedView ? { ...fromControls, ...savedView.filter } : fromControls;
  }, [filterControls, filterValues, savedViewOptions, savedViewId]);

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

      {savedViewOptions.length > 0 && (
        <div
          className="member-list__saved-views"
          data-testid="member-list-saved-views"
        >
          <label>
            Saved view
            <select
              data-testid="saved-view-picker"
              value={savedViewId}
              onChange={(e) => setSavedViewId(e.target.value)}
            >
              <option value="">All members</option>
              {savedViewOptions.map((view) => (
                <option key={view.id} value={view.id}>
                  {view.name}
                </option>
              ))}
            </select>
          </label>
        </div>
      )}

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
        // BifrostTable seeds its filter state from `defaultFilters` only on
        // mount, so remount it when the saved view changes to re-seed the
        // table — and therefore the issued GraphQL filter — from the new view.
        key={savedViewId}
        query={queryName}
        columns={columns}
        rowActions={rowActions}
        defaultFilters={tableFilter}
        onRowClick={(row) => navigate(`/members/${String(row.id)}`)}
      />
    </section>
  );
}
