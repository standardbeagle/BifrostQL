import { useBifrost } from './use-bifrost';
import type { DbSchemaProjection } from '@bifrostql/types';

/** Server-resolved policy for the current identity, see {@link usePolicy}. */
export interface UsePolicyResult {
  /** Whether the named table allows `action` (`read`, `create`, `update`, `delete`). */
  can: (action: string) => boolean;
  /** Whether the named column of the table may be read. */
  readable: (column: string) => boolean;
  /** Whether the named column of the table may be written. */
  writable: (column: string) => boolean;
  /** Effective grant names for the current identity (`_grants`). */
  grants: string[];
  isLoading: boolean;
  isError: boolean;
  error: Error | null;
  /** Re-fetch the projection, e.g. after the session changes. */
  refresh: () => void;
}

/** Wire shape of the policy query; mirrors the server-generated schema. */
interface PolicyQueryData {
  _grants: string[];
  _dbSchema?: DbSchemaProjection[];
}

const GRANTS_QUERY = 'query Policy { _grants }';

const TABLE_POLICY_QUERY =
  'query Policy($table: String!) { _grants _dbSchema(graphQlName: $table) { graphQlName allowedActions columns { graphQlName readable writable } } }';

/**
 * Reads the server-resolved policy projection for the current identity: the
 * caller's `_grants`, and — when a table is named — that table's
 * `allowedActions` and per-column `readable`/`writable` flags from `_dbSchema`.
 *
 * Every answer is the server's. Nothing here is derived from session claims or
 * client-side rules, so an affordance built on it agrees with what the server
 * will enforce. While loading, and for a table the server does not project,
 * every `can`/`readable`/`writable` answer is `false`.
 *
 * Must be used within a `BifrostProvider`.
 *
 * @param tableGraphQlName - GraphQL table name as `_dbSchema(graphQlName:)`
 *   accepts it. Omit to load grants only.
 */
export function usePolicy(tableGraphQlName?: string): UsePolicyResult {
  const result = useBifrost<PolicyQueryData>(
    tableGraphQlName ? TABLE_POLICY_QUERY : GRANTS_QUERY,
    tableGraphQlName ? { table: tableGraphQlName } : undefined,
  );
  const table = result.data?._dbSchema?.[0];
  const column = (name: string) =>
    table?.columns.find((item) => item.graphQlName === name);
  return {
    can: (action) => table?.allowedActions.includes(action) ?? false,
    readable: (name) => column(name)?.readable ?? false,
    writable: (name) => column(name)?.writable ?? false,
    grants: result.data?._grants ?? [],
    isLoading: result.isLoading,
    isError: result.isError,
    error: result.error,
    refresh: () => void result.refetch(),
  };
}
