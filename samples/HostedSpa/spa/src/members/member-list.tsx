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
import { ExportButton } from '../exports/export-button';
import { gateFinanceFields } from '../membership-plans/finance-fields';
import { useWriteFeedback, WriteFeedbackRegion } from '../common/write-feedback';

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

  // Columns are overlay-driven, then finance-gated for non-finance sessions so
  // the screen — and any CSV export of it — never names a policy-read-denied
  // column. `main.members` declares no finance columns today, so the gate is a
  // no-op here; it is applied for parity with the finance screens.
  const columns = useMemo(() => {
    if (!entity) {
      return [];
    }
    const built = buildColumns(entity);
    const allowed = gateFinanceFields(
      built.map((column) => column.field),
      MEMBERS_ENTITY_KEY,
      permissions,
    );
    return built.filter((column) => allowed.includes(column.field));
  }, [entity, permissions]);
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

  const feedback = useWriteFeedback();

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
          // A row action gives no other signal — without this the button
          // appears to work, the list refetches unchanged, and a denied write
          // is indistinguishable from a successful one.
          void feedback.run(
            () =>
              deactivate.mutateAsync({
                detail: { id: row.id, status: INACTIVE_STATUS },
              }),
            'Member deactivated.',
          );
        },
      });
    }
    return actions;
  }, [navigate, canWrite, deactivate, feedback]);

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

      <WriteFeedbackRegion feedback={feedback} testId="member-list-write" />

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

      <div className="member-list__actions" data-testid="member-list-actions">
        <ExportButton
          queryName={queryName}
          columns={columns}
          filter={tableFilter}
          fileName="members"
          testId="member-list-export"
        />
      </div>

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
        table={queryName}
        columns={columns}
        rowActions={rowActions}
        // Controlled: the saved view AND the ad-hoc filter controls both feed
        // `tableFilter`, so the table re-queries whenever either changes. The
        // previous `defaultFilter` + `key={savedViewId}` remount only re-seeded
        // on a saved-view change, leaving the filter inputs as dead UI while
        // ExportButton (which takes `filter` as a live prop) honoured them.
        filter={tableFilter}
        onRowClick={(row) => navigate(`/members/${String(row.id)}`)}
      />
    </section>
  );
}
