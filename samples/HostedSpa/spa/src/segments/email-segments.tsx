import { useMemo, useState } from 'react';
import {
  buildColumns,
  entityKeyToQueryName,
  useAppMetadata,
} from '@bifrostql/app-shell';
import { BifrostTable } from '@bifrostql/react';
import { getEmailSegmentOptions } from './segment-definitions';
import type { AppMetadataWithSegments } from './segment-definitions';

/** Stable test id prefix for the screen and its sub-states. */
const TEST_ID = 'email-segments';

/**
 * Email Segments screen.
 *
 * Lists the declarative email-segment definitions from the app-metadata
 * overlay's top-level `emailSegments` map (via {@link getEmailSegmentOptions})
 * and, for the selected segment, renders the matching audience by running the
 * segment's filter through the existing Bifrost query path — a read-only
 * {@link BifrostTable} over the segment's entity, columns derived from the
 * overlay exactly like the member roster and the dues reports.
 *
 * Because the audience is fetched through the ordinary Bifrost query path, the
 * host's `tenant-filter` module and column policy apply server-side, so the
 * list is tenant-scoped and permission-gated without any client-side predicate.
 *
 * This screen is intentionally definition-and-audience only: there is no
 * sending infrastructure — no SMTP, no queue, no send button — it produces the
 * audience list and nothing else.
 *
 * Must be mounted within an `AppShellProvider`, inside `AppLayout` /
 * `ProtectedRoute`.
 */
export function EmailSegments() {
  const { data, entities, isLoading, isError, error } = useAppMetadata();

  // `emailSegments` is an additive top-level overlay key not modeled on the
  // shared `AppMetadata` type, so the raw overlay document is read through the
  // segment-aware shape.
  const segments = useMemo(
    () => getEmailSegmentOptions(data as AppMetadataWithSegments | undefined),
    [data],
  );

  const [selectedId, setSelectedId] = useState<string>('');
  const selected = segments.find((segment) => segment.id === selectedId);

  // Columns for the selected segment's entity, overlay-driven — the audience
  // table names exactly the entity's overlay columns and nothing else.
  const columns = useMemo(() => {
    if (!selected) {
      return [];
    }
    const entity = entities[selected.entityKey];
    return entity ? buildColumns(entity) : [];
  }, [selected, entities]);

  const queryName = useMemo(
    () => (selected ? entityKeyToQueryName(selected.entityKey) : ''),
    [selected],
  );

  if (isLoading) {
    return <p data-testid={`${TEST_ID}-loading`}>Loading email segments…</p>;
  }

  if (isError) {
    return (
      <p role="alert" data-testid={`${TEST_ID}-error`}>
        Failed to load app metadata: {error?.message}
      </p>
    );
  }

  return (
    <section data-testid={TEST_ID}>
      <h2>Email Segments</h2>
      <p>
        Named, filter-based audience definitions. Selecting a segment shows who
        is in it — this screen produces the audience list only and does not
        send email.
      </p>

      {segments.length === 0 ? (
        <p data-testid={`${TEST_ID}-empty`}>
          No email segments are declared in the app-metadata overlay.
        </p>
      ) : (
        <ul data-testid={`${TEST_ID}-list`}>
          {segments.map((segment) => (
            <li key={segment.id}>
              <button
                type="button"
                data-testid={`${TEST_ID}-select-${segment.id}`}
                aria-pressed={segment.id === selectedId}
                onClick={() => setSelectedId(segment.id)}
              >
                {segment.name}
              </button>
            </li>
          ))}
        </ul>
      )}

      {selected && (
        <div data-testid={`${TEST_ID}-audience`}>
          <h3>Audience: {selected.name}</h3>
          {entities[selected.entityKey] ? (
            <BifrostTable
              query={queryName}
              columns={columns}
              defaultFilters={selected.filter}
            />
          ) : (
            <p role="alert" data-testid={`${TEST_ID}-audience-missing`}>
              The {selected.entityKey} entity is not declared in the
              app-metadata overlay.
            </p>
          )}
        </div>
      )}
    </section>
  );
}
