export type MutationType = 'insert' | 'update' | 'upsert' | 'delete';

function capitalize(s: string): string {
  return s.charAt(0).toUpperCase() + s.slice(1);
}

export function buildMutation(table: string, type: MutationType): string {
  const label = capitalize(type);
  return `mutation ${label}($detail: ${label}_${table}) { ${table}(${type}: $detail) }`;
}

export function buildInsertMutation(table: string): string {
  return buildMutation(table, 'insert');
}

export function buildUpdateMutation(table: string): string {
  return buildMutation(table, 'update');
}

export function buildUpsertMutation(table: string): string {
  return buildMutation(table, 'upsert');
}

export function buildDeleteMutation(table: string): string {
  return buildMutation(table, 'delete');
}
