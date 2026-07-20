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
  if (!plan) return <p style={styles.notice}>Related records unavailable: this relationship is incomplete.</p>;
  if (isLoading) return <p style={styles.notice}>Loading {subform.label}…</p>;
  if (error) return <p role="alert" style={styles.error}>Failed to load {subform.label}: {(error as Error).message}</p>;
  const rows = data?.[child.name]?.data ?? [];
  const deleteRow = async (childRow: Record<string, unknown>) => {
    const pk = pkFilterFor(childRow, child);
    if (!pk) return;
    await remove.deleteRow(pk);
    await queryClient.invalidateQueries({ queryKey: key });
  };
  const closeChildForm = async () => {
    setActiveForm(null);
    await queryClient.invalidateQueries({ queryKey: key });
  };
  return <section style={styles.root} aria-label={subform.label}>
    <h3 style={styles.title}>{subform.label}</h3>
    <button type="button" onClick={() => setActiveForm({ kind: 'create' })}>Add {subform.label}</button>
    {activeForm ? <div style={styles.childForm}>
      <FormRunnerView
        key={activeForm.kind === 'create' ? 'create' : rowIdOf(activeForm.row!, child, activeForm.index ?? 0)}
        table={child}
        definition={definition}
        hiddenFields={subform.childColumns}
        initialValues={activeForm.kind === 'create' ? prefill : undefined}
        startInNewMode={activeForm.kind === 'create'}
        initialPkRoute={activeForm.kind === 'edit' ? rowIdOf(activeForm.row!, child, activeForm.index ?? 0) : undefined}
        onClose={() => { void closeChildForm(); }}
      />
    </div> : rows.length === 0 ? <p style={styles.notice}>No related records.</p> : subform.mode === 'grid' ? (
      <table style={styles.table}><thead><tr>{child.columns.map((c) => <th key={c.name} style={styles.cell}>{c.label}</th>)}<th /></tr></thead><tbody>{rows.map((r, index) => <tr key={rowIdOf(r, child, index)}>{child.columns.map((c) => <td key={c.name} style={styles.cell}>{String(r[c.name] ?? '')}</td>)}<td><button type="button" onClick={() => setActiveForm({ kind: 'edit', row: r, index })}>Edit</button><button type="button" onClick={() => deleteRow(r)} disabled={remove.isPending}>Delete</button></td></tr>)}</tbody></table>
    ) : <div>{rows.map((r, index) => <article key={rowIdOf(r, child, index)} style={styles.card}>{child.columns.map((c) => <div key={c.name}><strong>{c.label}: </strong>{String(r[c.name] ?? '')}</div>)}<button type="button" onClick={() => setActiveForm({ kind: 'edit', row: r, index })}>Edit</button><button type="button" onClick={() => deleteRow(r)} disabled={remove.isPending}>Delete</button></article>)}</div>}
  </section>;
}
const styles: Record<string, React.CSSProperties> = { root: { marginTop: 20, borderTop: '1px solid #d1d5db', paddingTop: 12 }, title: { margin: '0 0 8px', fontSize: 15 }, notice: { color: '#6b7280', fontSize: 13 }, error: { color: '#b91c1c', fontSize: 13 }, table: { width: '100%', borderCollapse: 'collapse', fontSize: 13 }, cell: { textAlign: 'left', padding: 5, borderBottom: '1px solid #e5e7eb' }, card: { border: '1px solid #e5e7eb', borderRadius: 6, padding: 8, marginBottom: 8, fontSize: 13 }, childForm: { minHeight: 420, border: '1px solid #e5e7eb', borderRadius: 6, marginTop: 8 } };
