import { useBifrost } from '@bifrostql/react';

/** The identity fields of one `_dbSchema` table. */
export interface SchemaTableIdentity {
  schema: string;
  dbName: string;
  graphQlName: string;
}

/**
 * Find the GraphQL root field for an app-metadata entity key in the server's
 * `_dbSchema` table list.
 *
 * The server chooses GraphQL names (bare name, `ToGraphQl` cleanup, a schema
 * prefix only on a collision), so the client never rebuilds one. A qualified
 * key (`sales.orders`) matches schema and table exactly. A bare key matches by
 * table name and only when exactly one schema has that table: with two, the
 * key is ambiguous and resolves to nothing rather than to whichever came
 * first. Returns `null` when nothing (or more than one table) matches.
 */
export function resolveEntityQueryName(
  entityKey: string,
  tables: readonly SchemaTableIdentity[],
): string | null {
  const dot = entityKey.indexOf('.');
  if (dot !== -1) {
    const schema = entityKey.slice(0, dot);
    const table = entityKey.slice(dot + 1);
    const match = tables.find((t) => t.schema === schema && t.dbName === table);
    return match?.graphQlName ?? null;
  }
  const matches = tables.filter((t) => t.dbName === entityKey);
  return matches.length === 1 ? matches[0].graphQlName : null;
}

const SCHEMA_TABLES_QUERY =
  'query EntityTables { _dbSchema { schema dbName graphQlName } }';

/** Result of {@link useEntityQueryName}. */
export interface UseEntityQueryNameResult {
  /** The GraphQL root field, or `null` while loading, on error, or on no match. */
  queryName: string | null;
  isLoading: boolean;
  error: Error | null;
}

/**
 * Resolve an app-metadata entity key to the GraphQL root field the server
 * actually serves, via `_dbSchema` (see {@link resolveEntityQueryName}).
 * Must be used within a `BifrostProvider`.
 */
export function useEntityQueryName(
  entityKey: string,
): UseEntityQueryNameResult {
  const { data, isLoading, error } = useBifrost<{
    _dbSchema: SchemaTableIdentity[];
  }>(SCHEMA_TABLES_QUERY, undefined, { staleTime: Infinity });
  return {
    queryName: data ? resolveEntityQueryName(entityKey, data._dbSchema) : null,
    isLoading,
    error: error ?? null,
  };
}
