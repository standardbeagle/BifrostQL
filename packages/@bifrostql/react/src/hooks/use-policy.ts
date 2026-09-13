import { useBifrost } from './use-bifrost';
import type { DbSchemaProjection, GrantsProjection } from '@bifrostql/types';

/** Options for {@link usePolicy}. */
export interface UsePolicyOptions {
  /**
   * Who the answer is for. The projection is cached per identity, so a
   * signed-out to signed-in transition or a user switch in the same
   * QueryClient fetches again instead of reading the previous identity's
   * grants. Pass the session's stable user id; omit for an anonymous caller.
   */
  identity?: string;
  /** Whether the query should execute. Defaults to `true`. */
  enabled?: boolean;
}

/** Server-resolved policy for the current identity, see {@link usePolicy}. */
export interface UsePolicyResult {
  /** Whether the named table allows `action` (`read`, `create`, `update`, `delete`). */
  can: (action: string) => boolean;
  /**
   * Whether the projection names the column at all. A client-side computed
   * column or an alias is not in `_dbSchema`, so `readable`/`writable`
   * answer `false` for it without policy having said anything.
   */
  hasColumn: (column: string) => boolean;
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
  _grants: GrantsProjection;
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
 * will enforce. While loading, on error, and for a table the server does not
 * project, every `can`/`readable`/`writable` answer is `false`.
 *
 * The answer is fetched once per identity and never goes stale on its own:
 * a change of `options.identity` and `refresh()` are the only two ways to
 * fetch again. `@bifrostql/app-shell` wraps this hook with the session's
 * identity; other hosts pass their own.
 *
 * Must be used within a `BifrostProvider`.
 *
 * @param tableGraphQlName - GraphQL table name as `_dbSchema(graphQlName:)`
 *   accepts it. Omit to load grants only.
 * @param options - The identity the answer is for, and `enabled`.
 */
export function usePolicy(
  tableGraphQlName?: string,
  options: UsePolicyOptions = {},
): UsePolicyResult {
  const { identity, enabled } = options;
  const result = useBifrost<PolicyQueryData>(
    tableGraphQlName ? TABLE_POLICY_QUERY : GRANTS_QUERY,
    tableGraphQlName ? { table: tableGraphQlName } : undefined,
    { queryKeySuffix: identity ?? '', staleTime: Infinity, enabled },
  );
  const table = result.data?._dbSchema?.[0];
  const column = (name: string) =>
    table?.columns.find((item) => item.graphQlName === name);
  return {
    can: (action) => table?.allowedActions.includes(action) ?? false,
    hasColumn: (name) => column(name) !== undefined,
    readable: (name) => column(name)?.readable ?? false,
    writable: (name) => column(name)?.writable ?? false,
    grants: result.data?._grants ?? [],
    isLoading: result.isLoading,
    isError: result.isError,
    error: result.error,
    refresh: () => void result.refetch(),
  };
}
