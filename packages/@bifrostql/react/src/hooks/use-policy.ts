import { useContext } from 'react';
import { useBifrost } from './use-bifrost';
import { BifrostContext } from '../components/bifrost-provider';
import type {
  DbSchemaProjection,
  GrantsProjection,
} from '@bifrostql/types';

export interface UsePolicyResult {
  can: (action: string) => boolean;
  readable: (column: string) => boolean;
  writable: (column: string) => boolean;
  grants: string[];
  isLoading: boolean;
  isError: boolean;
  error: Error | null;
  refresh: () => void;
}

export function usePolicy(qualifiedTable?: string): UsePolicyResult {
  const config = useContext(BifrostContext);
  const identity = config?.getToken ? 'token' : 'session';
  const query = `query Policy($table: String) { _dbSchema(graphQlName: $table) { allowedActions columns { name readable writable } } _grants { grants } }`;
  const result = useBifrost<{
    _dbSchema?: DbSchemaProjection & { columns: Array<DbSchemaProjection['columns'][string] & { name: string }> };
    _grants?: GrantsProjection;
  }>(query, { table: qualifiedTable }, { enabled: Boolean(qualifiedTable) });
  const schema = result.data?._dbSchema;
  const columns = schema?.columns;
  const column = (name: string) =>
    Array.isArray(columns) ? columns.find((item) => item.name === name) : columns?.[name];
  return {
    can: (action) => schema?.allowedActions?.includes(action) ?? false,
    readable: (name) => column(name)?.readable ?? false,
    writable: (name) => column(name)?.writable ?? false,
    grants: result.data?._grants?.grants ?? [],
    isLoading: result.isLoading,
    isError: result.isError,
    error: (result.error as Error | null) ?? null,
    refresh: () => void result.refetch(),
  };
}
