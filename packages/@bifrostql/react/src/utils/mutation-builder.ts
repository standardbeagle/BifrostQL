import { assertGraphqlName } from './graphql-identifiers';

/** The four mutation operations supported by BifrostQL. */
export type MutationType = 'insert' | 'update' | 'upsert' | 'delete';

function capitalize(s: string): string {
  return s.charAt(0).toUpperCase() + s.slice(1);
}

/**
 * Build a GraphQL mutation string for a BifrostQL table.
 *
 * @param table - The database table name.
 * @param type - The mutation operation type.
 * @returns A parameterized GraphQL mutation string with a `$detail` variable.
 *
 * @example
 * ```ts
 * buildMutation('users', 'insert');
 * // => 'mutation Insert($detail: Insert_users) { users(insert: $detail) }'
 * ```
 */
export function buildMutation(table: string, type: MutationType): string {
  assertGraphqlName(table, 'table');
  const label = capitalize(type);
  return `mutation ${label}($detail: ${label}_${table}) { ${table}(${type}: $detail) }`;
}

/**
 * Build an insert mutation string for a BifrostQL table.
 * Shorthand for `buildMutation(table, 'insert')`.
 *
 * @param table - The database table name.
 */
export function buildInsertMutation(table: string): string {
  return buildMutation(table, 'insert');
}

/**
 * Build an update mutation string for a BifrostQL table.
 * Shorthand for `buildMutation(table, 'update')`.
 *
 * @param table - The database table name.
 */
export function buildUpdateMutation(table: string): string {
  return buildMutation(table, 'update');
}

/**
 * Build an upsert mutation string for a BifrostQL table.
 * Shorthand for `buildMutation(table, 'upsert')`.
 *
 * @param table - The database table name.
 */
export function buildUpsertMutation(table: string): string {
  return buildMutation(table, 'upsert');
}

/**
 * Build a delete mutation string for a BifrostQL table.
 * Shorthand for `buildMutation(table, 'delete')`.
 *
 * @param table - The database table name.
 */
export function buildDeleteMutation(table: string): string {
  return buildMutation(table, 'delete');
}

/**
 * Build the mutation for a table's server-side `<table>_batch` field: every
 * action in `$actions` runs in one transaction, and the field replies with the
 * affected-row count.
 *
 * @param table - The database table name.
 *
 * @example
 * ```ts
 * buildBatchMutation('users');
 * // => 'mutation Batch($actions: [batch_users!]!) { users_batch(actions: $actions) }'
 * ```
 */
export function buildBatchMutation(table: string): string {
  assertGraphqlName(table, 'table');
  return `mutation Batch($actions: [batch_${table}!]!) { ${table}_batch(actions: $actions) }`;
}
