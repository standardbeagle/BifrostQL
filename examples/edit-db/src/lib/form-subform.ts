import type { Join, Table } from '../types/schema';
import { coerceForGql } from './fk';

export interface ChildFilterPlan {
  params: string[];
  filter: string;
  variables: Record<string, unknown>;
}

/** True only when every parent-key/FK pair can be safely bound. */
export function relationshipIsUsable(parent: Table, child: Table, join: Join): boolean {
  return join.sourceColumnNames.length > 0
    && join.sourceColumnNames.length === join.destinationColumnNames.length
    && join.sourceColumnNames.every((name) => parent.columns.some((c) => c.name === name))
    && join.destinationColumnNames.every((name) => child.columns.some((c) => c.name === name));
}

/** Builds a parameterized child filter; composite relationships bind every pair. */
export function childFilterPlan(parent: Table, child: Table, join: Join, row: Record<string, unknown> | null): ChildFilterPlan | null {
  if (!row || !relationshipIsUsable(parent, child, join)) return null;
  const params: string[] = [];
  const clauses: string[] = [];
  const variables: Record<string, unknown> = {};
  for (let i = 0; i < join.sourceColumnNames.length; i++) {
    const source = join.sourceColumnNames[i];
    const destination = join.destinationColumnNames[i];
    const value = row[source];
    if (value === undefined || value === null) return null;
    const type = child.columns.find((c) => c.name === destination)?.paramType.replace('!', '') ?? 'String';
    const variable = `parent_${source}`;
    params.push(`$${variable}: ${type}`);
    clauses.push(`{${destination}: {_eq: $${variable}}}`);
    variables[variable] = coerceForGql(value, type);
  }
  return { params, filter: clauses.length === 1 ? clauses[0] : `{and: [${clauses.join(', ')}]}`, variables };
}

/** Values supplied to a child create form; callers hide these FK controls. */
export function childPrefill(join: Join, row: Record<string, unknown> | null): Record<string, unknown> {
  if (!row || join.sourceColumnNames.length !== join.destinationColumnNames.length) return {};
  return Object.fromEntries(join.destinationColumnNames.map((destination, i) => [destination, row[join.sourceColumnNames[i]]]));
}
