/**
 * Runtime renderer for a saved form (task 2.2).
 *
 * Binds a {@link FormDefinition} — the builder's output — to live data over its
 * one table: browse records (first/prev/next/last + jump-to-PK), create/update/
 * delete through the shipped mutation hooks, client validation via
 * field-validation, and definition-driven widgets that honour read-only/visible.
 *
 * Everything routes through `useFetcher()` (via useSchema / useTableMutation /
 * useDeleteMutation and the browse query), so the desktop shell's HTTP↔binary
 * transport toggle covers the runner exactly as it covers the editor. All PK
 * handling goes through row-id.ts / query-builder.ts, so composite primary keys
 * work without any first-key-only shortcut.
 */

import { useCallback, useEffect, useMemo, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useFetcher } from '../common/fetcher';
import { useSchema } from '../hooks/useSchema';
import { useTableMutation } from '../hooks/useTableMutation';
import { useDeleteMutation } from '../hooks/useDeleteMutation';
import type { Column, Schema, Table } from '../types/schema';
import {
  rowIdOf,
  pkFilterFor,
  parsePkRoute,
  buildPkEqFilter,
  encodePkRoute,
  type PkFilter,
} from '../lib/row-id';
import { assertGraphQlName, buildSingleRowQuery } from '../lib/query-builder';
import { validateFieldValue } from '../lib/field-validation';
import { isDateColumn, toDateInputValue } from '../lib/date-input';
import {
  visibleFields,
  type FormDefinition,
  type FormField,
} from '../lib/form-definition';
import {
  resolveWidget,
  controlRender,
  type FieldWidgetHint,
} from '../lib/form-widget';
import { nextIndex, canNavigate, positionLabel, type NavDirection } from '../lib/form-runner-nav';

/** Unqualified suffix of a possibly "schema.name"-qualified identifier. */
function unqualified(id: string): string {
  const dot = id.lastIndexOf('.');
  return dot >= 0 ? id.slice(dot + 1) : id;
}

/**
 * Resolves the definition's bound table against the introspected schema. The
 * builder may store a qualified "schema.name" while the schema keys on the
 * GraphQL name, so match on either form. Returns undefined when the table is no
 * longer in the published schema.
 */
export function resolveDefinitionTable(schema: Schema, tableId: string): Table | undefined {
  const direct = schema.findTable(tableId);
  if (direct) return direct;
  const unq = unqualified(tableId);
  return schema.data.find(
    (t) => t.graphQlName === tableId || t.dbName === tableId || t.graphQlName === unq || t.dbName === unq,
  );
}

/** A definition field paired with its resolved schema column and effective widget. */
interface BoundField {
  field: FormField;
  column: Column;
  control: ReturnType<typeof resolveWidget>['control'];
  readOnly: boolean;
}

/** Where the runner is currently pointed: an absolute row offset, or a pinned PK route. */
type Location = { kind: 'offset'; index: number } | { kind: 'pk'; route: string };

interface BrowseResponse {
  [table: string]: { total: number; data: Record<string, unknown>[] };
}
interface SingleRowResponse {
  value: { data: Record<string, unknown>[] };
}

/** Selection field list for a row read: every displayed column plus all PK columns. */
function selectFields(bound: BoundField[], table: Table): string[] {
  const names = new Set<string>();
  for (const b of bound) names.add(b.column.name);
  for (const pk of table.primaryKeys ?? []) names.add(pk);
  return [...names];
}

/** The paged browse query: one row at an absolute offset, plus the total count. */
function buildBrowseQuery(table: Table, fields: readonly string[]): string {
  assertGraphQlName(table.name, 'form browse table name');
  for (const f of fields) assertGraphQlName(f, 'form browse selection field');
  return `query FormBrowse_${table.name}($limit: Int, $offset: Int) { ${table.name}(limit: $limit offset: $offset) { total data { ${fields.join(' ')} } } }`;
}

/** Coerces a stored value into what the control's input element expects. */
function toInputValue(control: BoundField['control'], column: Column, raw: unknown): string | boolean {
  if (control === 'checkbox') return !!raw;
  if ((control === 'date' || control === 'datetime') && (raw == null || typeof raw === 'string')) {
    return toDateInputValue(raw ?? undefined, control === 'datetime' || (isDateColumn(column) && false));
  }
  return raw == null ? '' : String(raw);
}

interface ServerErrorMap {
  fields: Record<string, string>;
  form: string | null;
}

/**
 * Splits a mutation error into per-field messages (when the GraphQL error path
 * ends at a displayed column) and a form-level remainder. A transport that drops
 * the error path (binary/joined-message) falls back to a single form-level
 * message — best-effort, never swallowed.
 */
function mapServerError(error: unknown, columnNames: Set<string>): ServerErrorMap {
  const out: ServerErrorMap = { fields: {}, form: null };
  const errs = (error as { errors?: { message: string; path?: (string | number)[] }[] })?.errors;
  if (Array.isArray(errs)) {
    const formParts: string[] = [];
    for (const e of errs) {
      const leaf = e.path?.[e.path.length - 1];
      if (typeof leaf === 'string' && columnNames.has(leaf)) out.fields[leaf] = e.message;
      else formParts.push(e.message);
    }
    out.form = formParts.length > 0 ? formParts.join('; ') : null;
    return out;
  }
  out.form = error instanceof Error ? error.message : String(error);
  return out;
}

interface FormRunnerViewProps {
  table: Table;
  definition: FormDefinition;
  /** App-metadata widget hints keyed by column name (widget / visible / readOnly). */
  fieldMetadata?: Record<string, FieldWidgetHint>;
  onClose?: () => void;
}

/**
 * The bound runtime for a resolved table. Split from {@link FormRunner} so it can
 * be exercised directly with a `Table` and a fetcher, without a schema fetch.
 */
export function FormRunnerView({ table, definition, fieldMetadata, onClose }: FormRunnerViewProps) {
  const fetcher = useFetcher();
  const queryClient = useQueryClient();

  const columnByName = useMemo(
    () => new Map(table.columns.map((c) => [c.name, c] as const)),
    [table],
  );

  // Displayed fields: included + visible + still present in the schema.
  const [location, setLocation] = useState<Location>({ kind: 'offset', index: 0 });
  const [mode, setMode] = useState<'browse' | 'new'>('browse');
  const [draft, setDraft] = useState<Record<string, unknown>>({});
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [jump, setJump] = useState('');
  const [total, setTotal] = useState(0);

  const isNew = mode === 'new';

  const bound = useMemo<BoundField[]>(() => {
    return visibleFields(definition)
      .map((field) => {
        const column = columnByName.get(field.column);
        if (!column) return null;
        const w = resolveWidget(field, fieldMetadata?.[field.column]);
        if (!w.visible) return null;
        return { field, column, control: w.control, readOnly: w.readOnly };
      })
      .filter((b): b is BoundField => b !== null);
  }, [definition, columnByName, fieldMetadata]);

  // Whether a field is editable in the current mode. Identity/computed columns
  // are never editable; a PK is locked while browsing (its value is the WHERE
  // key) but editable when creating a client-supplied (e.g. composite) key.
  const isEditable = useCallback(
    (b: BoundField): boolean => {
      if (b.readOnly || b.column.isReadOnly || b.column.isIdentity) return false;
      if (b.column.isPrimaryKey && !isNew) return false;
      return true;
    },
    [isNew],
  );

  const editColumns = useMemo(
    () => bound.filter(isEditable).map((b) => ({ column: b.column })),
    [bound, isEditable],
  );
  const idColumns = useMemo(
    () => (table.primaryKeys ?? []).map((pk) => columnByName.get(pk)).filter((c): c is Column => !!c),
    [table, columnByName],
  );
  const editColumnNames = useMemo(
    () => new Set(editColumns.map((e) => e.column.name)),
    [editColumns],
  );

  const fields = useMemo(() => selectFields(bound, table), [bound, table]);

  // The active-row read: an offset browse (row + total) or a pinned by-PK lookup.
  const rowQuery = useMemo(() => {
    if (isNew || bound.length === 0) return null;
    if (location.kind === 'offset') {
      return { text: buildBrowseQuery(table, fields), variables: { limit: 1, offset: location.index }, kind: 'offset' as const };
    }
    const pkFilter = parsePkRoute(location.route, table);
    const pkEq = pkFilter ? buildPkEqFilter(pkFilter, table) : null;
    if (!pkEq) return null;
    return { text: buildSingleRowQuery(table, pkEq, fields), variables: pkEq.variables, kind: 'pk' as const };
  }, [isNew, bound.length, location, table, fields]);

  const { data: rowData, isLoading, error: rowError } = useQuery({
    queryKey: ['formRunner', table.name, location, isNew],
    queryFn: () => fetcher.query<BrowseResponse | SingleRowResponse>(rowQuery!.text, rowQuery!.variables),
    enabled: !isNew && rowQuery !== null,
  });

  // Unwrap the active row + total from whichever read shape came back.
  const currentRow = useMemo<Record<string, unknown> | null>(() => {
    if (!rowData || !rowQuery) return null;
    if (rowQuery.kind === 'offset') {
      const page = (rowData as BrowseResponse)[table.name];
      return page?.data?.[0] ?? null;
    }
    return (rowData as SingleRowResponse).value?.data?.[0] ?? null;
  }, [rowData, rowQuery, table.name]);

  // Track the total from offset reads so the position label stays accurate.
  useEffect(() => {
    if (rowData && rowQuery?.kind === 'offset') {
      const page = (rowData as BrowseResponse)[table.name];
      if (page) setTotal(page.total);
    }
  }, [rowData, rowQuery, table.name]);

  // Seed the editable draft whenever the loaded row (or new-mode) changes.
  useEffect(() => {
    setFieldErrors({});
    setFormError(null);
    if (isNew) {
      setDraft({});
      return;
    }
    const next: Record<string, unknown> = {};
    for (const b of bound) next[b.column.name] = currentRow?.[b.column.name] ?? null;
    setDraft(next);
  }, [currentRow, isNew, bound]);

  const currentIndex = location.kind === 'offset' ? location.index : -1;

  // editId drives the update WHERE clause (route-encoded, composite-safe).
  const editId = useMemo(() => {
    if (isNew || !currentRow) return '';
    return rowIdOf(currentRow, table, currentIndex < 0 ? 0 : currentIndex);
  }, [isNew, currentRow, table, currentIndex]);

  const mutation = useTableMutation(table, editColumns, idColumns, editId);
  const del = useDeleteMutation(table);

  const navigate = useCallback(
    (dir: NavDirection) => {
      setMode('browse');
      setLocation((loc) => {
        const from = loc.kind === 'offset' ? loc.index : 0;
        return { kind: 'offset', index: nextIndex(from, total, dir) };
      });
    },
    [total],
  );

  const startNew = useCallback(() => {
    setMode('new');
  }, []);

  const doJump = useCallback(() => {
    const route = jump.trim();
    if (route === '') return;
    setMode('browse');
    setLocation({ kind: 'pk', route });
  }, [jump]);

  const setField = useCallback((name: string, value: unknown) => {
    setDraft((d) => ({ ...d, [name]: value }));
  }, []);

  const save = useCallback(async () => {
    // Client validation mirrors the server rules before any write.
    const errs: Record<string, string> = {};
    for (const b of bound) {
      if (!isEditable(b)) continue;
      const required = b.field.required || !b.column.isNullable;
      const msg = validateFieldValue(b.column, draft[b.column.name], required);
      if (msg) errs[b.column.name] = msg;
    }
    if (Object.keys(errs).length > 0) {
      setFieldErrors(errs);
      setFormError(null);
      return;
    }
    setFieldErrors({});
    setFormError(null);

    // Only the editable columns travel in the detail; the update path re-derives
    // the PK from editId, and the insert path carries any client-supplied key.
    const detail: Record<string, unknown> = {};
    for (const name of editColumnNames) detail[name] = draft[name];

    try {
      if (isNew) {
        // insert() resolves to the mutation's data payload `{ <table>: <scalar> }`;
        // the scalar is the generated identity for a single identity PK.
        const payload = (await mutation.insert(detail)) as Record<string, unknown> | null;
        const identity = payload?.[table.name];
        // Land on the created row. A single identity PK comes back as that
        // scalar; otherwise (composite / client-supplied) the key values are
        // the ones we just submitted.
        const pk: PkFilter = {};
        const keys = table.primaryKeys ?? [];
        for (const k of keys) {
          const col = columnByName.get(k);
          pk[k] = keys.length === 1 && col?.isIdentity ? identity : draft[k];
        }
        setMode('browse');
        setLocation({ kind: 'pk', route: encodePkRoute(pk, table) });
      } else {
        await mutation.update(detail);
        // Re-read so the displayed row reflects what was persisted.
        await queryClient.invalidateQueries({ queryKey: ['formRunner', table.name] });
      }
    } catch (e) {
      const mapped = mapServerError(e, editColumnNames);
      setFieldErrors(mapped.fields);
      setFormError(mapped.form);
    }
  }, [bound, isEditable, draft, editColumnNames, isNew, mutation, table, columnByName, queryClient]);

  const remove = useCallback(async () => {
    const pk = pkFilterFor(currentRow, table);
    if (!pk) return;
    try {
      await del.deleteRow(pk);
      setMode('browse');
      setLocation({ kind: 'offset', index: Math.max(0, (currentIndex < 0 ? 0 : currentIndex) - 1) });
      await queryClient.invalidateQueries({ queryKey: ['formRunner', table.name] });
    } catch (e) {
      setFormError(e instanceof Error ? e.message : String(e));
    }
  }, [currentRow, table, del, currentIndex, queryClient]);

  if (bound.length === 0) {
    return <div style={styles.notice}>This form has no fields that match the current schema.</div>;
  }

  const positionText =
    location.kind === 'pk' ? `key ${location.route}` : positionLabel(currentIndex, total);
  const noRecords = !isNew && location.kind === 'offset' && total === 0 && !isLoading;

  return (
    <div style={styles.root}>
      <div style={styles.toolbar} role="toolbar" aria-label="Record navigation">
        <button type="button" style={styles.btn} onClick={() => navigate('first')} disabled={isNew || !canNavigate(currentIndex, total, 'first')} aria-label="First record">⏮</button>
        <button type="button" style={styles.btn} onClick={() => navigate('prev')} disabled={isNew || !canNavigate(currentIndex, total, 'prev')} aria-label="Previous record">◀</button>
        <span style={styles.position} aria-label="Record position">{isNew ? 'New record' : positionText}</span>
        <button type="button" style={styles.btn} onClick={() => navigate('next')} disabled={isNew || !canNavigate(currentIndex, total, 'next')} aria-label="Next record">▶</button>
        <button type="button" style={styles.btn} onClick={() => navigate('last')} disabled={isNew || !canNavigate(currentIndex, total, 'last')} aria-label="Last record">⏭</button>
        <span style={styles.spacer} />
        <input
          style={styles.jump}
          value={jump}
          onChange={(e) => setJump(e.target.value)}
          placeholder="Jump to key…"
          aria-label="Jump to key"
        />
        <button type="button" style={styles.btn} onClick={doJump} aria-label="Go to key">Go</button>
        <button type="button" style={styles.btn} onClick={startNew} aria-label="New record">New</button>
        {onClose && <button type="button" style={styles.btn} onClick={onClose} aria-label="Close form">✕</button>}
      </div>

      <div style={styles.body}>
        <h2 style={styles.title}>{definition.title}</h2>
        {formError && <div role="alert" style={styles.formError}>{formError}</div>}
        {rowError && <div role="alert" style={styles.formError}>Failed to load record: {(rowError as Error).message}</div>}
        {noRecords ? (
          <p style={styles.notice}>No records. Use “New” to create one.</p>
        ) : (
          <div style={{ ...styles.grid, gridTemplateColumns: `repeat(${definition.columns}, minmax(0, 1fr))` }}>
            {bound.map((b) => {
              const editable = isEditable(b);
              const value = draft[b.column.name];
              const err = fieldErrors[b.column.name];
              const required = b.field.required || !b.column.isNullable;
              return (
                <label key={b.column.name} style={styles.field}>
                  <span style={styles.label}>
                    {b.field.label}
                    {required && <span style={styles.req}> *</span>}
                  </span>
                  {renderControl(b, value, editable, required, setField)}
                  {err && <span role="alert" style={styles.fieldError}>{err}</span>}
                </label>
              );
            })}
          </div>
        )}
      </div>

      <div style={styles.footer}>
        <button type="button" style={styles.primaryBtn} onClick={save} disabled={mutation.isPending || (noRecords && !isNew)}>
          {isNew ? 'Create' : 'Save'}
        </button>
        {!isNew && !noRecords && (
          <button type="button" style={styles.btn} onClick={remove} disabled={del.isPending}>Delete</button>
        )}
      </div>
    </div>
  );
}

function renderControl(
  b: BoundField,
  value: unknown,
  editable: boolean,
  required: boolean,
  setField: (name: string, value: unknown) => void,
): React.ReactNode {
  const name = b.column.name;
  const render = controlRender(b.control);
  const common = {
    id: name,
    'aria-label': b.field.label,
    disabled: !editable,
    style: styles.control,
  } as const;

  if (render.kind === 'checkbox') {
    return (
      <input
        {...common}
        type="checkbox"
        checked={!!toInputValue('checkbox', b.column, value)}
        onChange={(e) => setField(name, e.target.checked)}
      />
    );
  }
  if (render.kind === 'textarea') {
    return (
      <textarea
        {...common}
        rows={3}
        required={required}
        value={String(toInputValue('text', b.column, value))}
        onChange={(e) => setField(name, e.target.value)}
      />
    );
  }
  if (render.kind === 'select') {
    const options = b.column.enumValues ?? [];
    const labels = b.column.enumLabels && b.column.enumLabels.length === options.length ? b.column.enumLabels : options;
    return (
      <select
        {...common}
        value={String(toInputValue('text', b.column, value))}
        onChange={(e) => setField(name, e.target.value === '' ? null : e.target.value)}
      >
        {!required && <option value="">(none)</option>}
        {options.map((opt, i) => (
          <option key={opt} value={opt}>{labels[i] ?? opt}</option>
        ))}
      </select>
    );
  }
  return (
    <input
      {...common}
      type={render.type}
      required={required}
      value={String(toInputValue(b.control, b.column, value))}
      onChange={(e) => setField(name, e.target.value)}
    />
  );
}

interface FormRunnerProps {
  definition: FormDefinition;
  fieldMetadata?: Record<string, FieldWidgetHint>;
  onClose?: () => void;
}

/**
 * Top-level form runner: resolves the definition's table from the introspected
 * schema, then hands off to {@link FormRunnerView}. Renders loading / error /
 * not-found states so a stale definition cannot crash the tree.
 */
export function FormRunner({ definition, fieldMetadata, onClose }: FormRunnerProps) {
  const schema = useSchema();
  if (schema.loading) return <div style={styles.notice}>Loading schema…</div>;
  if (schema.error) return <div style={styles.notice}>Could not load schema: {schema.error.message}</div>;
  const table = resolveDefinitionTable(schema, definition.table);
  if (!table) {
    return <div style={styles.notice}>Table “{definition.table}” is not in the current schema.</div>;
  }
  return <FormRunnerView table={table} definition={definition} fieldMetadata={fieldMetadata} onClose={onClose} />;
}

const styles: Record<string, React.CSSProperties> = {
  root: { display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0 },
  toolbar: { display: 'flex', alignItems: 'center', gap: 6, padding: '8px 12px', borderBottom: '1px solid var(--border, #d1d5db)', flexWrap: 'wrap' },
  position: { fontSize: 13, color: '#374151', minWidth: 80, textAlign: 'center' },
  spacer: { flex: 1 },
  jump: { padding: '4px 6px', border: '1px solid var(--border, #d1d5db)', borderRadius: 6, font: 'inherit', fontSize: 13, width: 120 },
  body: { flex: 1, minHeight: 0, overflow: 'auto', padding: 16 },
  title: { margin: '0 0 16px', fontSize: 18 },
  grid: { display: 'grid', gap: 14, maxWidth: 720 },
  field: { display: 'flex', flexDirection: 'column', gap: 4, minWidth: 0 },
  label: { fontSize: 12, fontWeight: 600, color: '#374151' },
  control: { padding: '6px 8px', border: '1px solid var(--border, #d1d5db)', borderRadius: 6, font: 'inherit', fontSize: 13 },
  footer: { display: 'flex', gap: 8, padding: '8px 12px', borderTop: '1px solid var(--border, #d1d5db)' },
  btn: { border: '1px solid var(--border, #d1d5db)', background: 'transparent', borderRadius: 6, padding: '6px 12px', cursor: 'pointer', font: 'inherit' },
  primaryBtn: { border: '1px solid #2563eb', background: '#2563eb', color: '#fff', borderRadius: 6, padding: '6px 16px', cursor: 'pointer', font: 'inherit' },
  req: { color: '#dc2626' },
  formError: { background: '#fef2f2', border: '1px solid #fecaca', color: '#b91c1c', borderRadius: 6, padding: '8px 10px', marginBottom: 12, fontSize: 13 },
  fieldError: { color: '#dc2626', fontSize: 12 },
  notice: { padding: 24, color: '#6b7280' },
};
