import { useMemo, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useFetcher } from '../common/fetcher';
import { useDeleteMutation } from '../hooks/useDeleteMutation';
import type { Join, Table } from '../types/schema';
import type { FormDefinition, FormSubform } from '../lib/form-definition';
import { childFilterPlan, childPrefill } from '../lib/form-subform';
import { assertGraphQlName } from '../lib/query-builder';
import { pkFilterFor, rowIdOf } from '../lib/row-id';
import { FormRunnerView } from './form-runner';
import { ConfirmDialog } from './confirm-dialog';

interface Props { parent: Table; child: Table; subform: FormSubform; row: Record<string, unknown>; }

function childFormDefinition(child: Table): FormDefinition {
  return {
    table: child.name,
    title: child.label,
    columns: 1,
    fields: child.columns.map((column) => ({
      column: column.name,
      label: column.label,
      control: column.paramType.startsWith('Boolean') ? 'checkbox' : column.paramType.startsWith('Int') || column.paramType.startsWith('Float') || column.paramType.startsWith('Decimal') ? 'number' : 'text',
      readOnly: column.isReadOnly || column.isIdentity,
      required: !column.isNullable,
      include: true,
    })),
  };
}

/** Related child rows in a form. The selected parent values are always passed as GraphQL variables. */
export function FormSubformPanel({ parent, child, subform, row }: Props) {
  const fetcher = useFetcher();
  const queryClient = useQueryClient();
  const remove = useDeleteMutation(child);
  const [activeForm, setActiveForm] = useState<{ kind: 'create' | 'edit'; row?: Record<string, unknown>; index?: number } | null>(null);
  // Delete asks first, and a refusal or server error is shown, never dropped.
  const [pendingDelete, setPendingDelete] = useState<Record<string, unknown> | null>(null);
  const [deleteError, setDeleteError] = useState<string | null>(null);
  const join = useMemo<Join>(() => ({ name: subform.relationship, fieldName: subform.relationship, sourceColumnNames: subform.parentColumns, destinationTable: child.name, destinationColumnNames: subform.childColumns }), [subform, child.name]);
  const plan = useMemo(() => childFilterPlan(parent, child, join, row), [parent, child, join, row]);
  const fields = useMemo(() => child.columns.map((c) => { assertGraphQlName(c.name, 'form subform field'); return c.name; }), [child]);
  const query = useMemo(() => {
    if (!plan) return null;
    assertGraphQlName(child.name, 'form subform table');
    return `query FormSubform_${child.name}(${plan.params.join(', ')}) { ${child.name}(filter: ${plan.filter}) { data { ${fields.join(' ')} } } }`;
  }, [child.name, fields, plan]);
  const key = ['formSubform', parent.name, child.name, rowIdOf(row, parent, 0)];
  const { data, isLoading, error } = useQuery({ queryKey: key, queryFn: () => fetcher.query<Record<string, { data: Record<string, unknown>[] }>>(query!, plan!.variables), enabled: !!query });
  const definition = useMemo(() => childFormDefinition(child), [child]);
  const prefill = useMemo(() => childPrefill(join, row), [join, row]);
  if (!plan) return <p className={classes.notice}>Related records unavailable: this relationship is incomplete.</p>;
  if (isLoading) return <p className={classes.notice}>Loading {subform.label}…</p>;
  if (error) return <p role="alert" className={classes.error}>Failed to load {subform.label}: {(error as Error).message}</p>;
  const rows = data?.[child.name]?.data ?? [];
  const deleteRow = async (childRow: Record<string, unknown>) => {
    setDeleteError(null);
    const pk = pkFilterFor(childRow, child);
    if (!pk) {
      setDeleteError("This record has no complete key, so it can't be deleted.");
      return;
    }
    try {
      await remove.deleteRow(pk);
    } catch (e) {
      setDeleteError(`Delete failed: ${e instanceof Error ? e.message : String(e)}`);
      return;
    }
    await queryClient.invalidateQueries({ queryKey: key });
  };
  const closeChildForm = async () => {
    setActiveForm(null);
    await queryClient.invalidateQueries({ queryKey: key });
  };
  return <section className={classes.root} aria-label={subform.label}>
    <h3 className={classes.title}>{subform.label}</h3>
    <button type="button" onClick={() => setActiveForm({ kind: 'create' })}>Add {subform.label}</button>
    {deleteError && <p role="alert" className={classes.error}>{deleteError}</p>}
    {activeForm ? <div className={classes.childForm}>
      <FormRunnerView
        key={activeForm.kind === 'create' ? 'create' : rowIdOf(activeForm.row!, child, activeForm.index ?? 0)}
        table={child}
        definition={definition}
        hiddenFields={subform.childColumns}
        scope={plan}
        initialValues={activeForm.kind === 'create' ? prefill : undefined}
        startInNewMode={activeForm.kind === 'create'}
        initialPkRoute={activeForm.kind === 'edit' ? rowIdOf(activeForm.row!, child, activeForm.index ?? 0) : undefined}
        onClose={() => { void closeChildForm(); }}
      />
    </div> : rows.length === 0 ? <p className={classes.notice}>No related records.</p> : subform.mode === 'grid' ? (
      <table className={classes.table}><thead><tr>{child.columns.map((c) => <th key={c.name} className={classes.cell}>{c.label}</th>)}<th /></tr></thead><tbody>{rows.map((r, index) => <tr key={rowIdOf(r, child, index)}>{child.columns.map((c) => <td key={c.name} className={classes.cell}>{String(r[c.name] ?? '')}</td>)}<td><button type="button" onClick={() => setActiveForm({ kind: 'edit', row: r, index })}>Edit</button><button type="button" onClick={() => setPendingDelete(r)} disabled={remove.isPending}>Delete</button></td></tr>)}</tbody></table>
    ) : <div>{rows.map((r, index) => <article key={rowIdOf(r, child, index)} className={classes.card}>{child.columns.map((c) => <div key={c.name}><strong>{c.label}: </strong>{String(r[c.name] ?? '')}</div>)}<button type="button" onClick={() => setActiveForm({ kind: 'edit', row: r, index })}>Edit</button><button type="button" onClick={() => setPendingDelete(r)} disabled={remove.isPending}>Delete</button></article>)}</div>}
    <ConfirmDialog
      open={pendingDelete !== null}
      onOpenChange={(open) => { if (!open) setPendingDelete(null); }}
      title={`Delete this ${child.label ?? child.name} record?`}
      description="This can't be undone."
      confirmLabel="Delete"
      variant="destructive"
      isPending={remove.isPending}
      onConfirm={() => { const target = pendingDelete; setPendingDelete(null); if (target) void deleteRow(target); }}
    />
  </section>;
}
// Theme-token classes (see form-runner.tsx): follows light/dark mode.
const classes = {
  root: 'mt-5 pt-3 border-t border-border',
  title: 'mb-2 text-[15px]',
  notice: 'text-[13px] text-muted-foreground',
  error: 'text-[13px] text-destructive',
  table: 'w-full border-collapse text-[13px]',
  cell: 'p-[5px] text-left border-b border-border',
  card: 'mb-2 p-2 border border-border rounded-md text-[13px]',
  childForm: 'min-h-[420px] mt-2 border border-border rounded-md',
} as const;
