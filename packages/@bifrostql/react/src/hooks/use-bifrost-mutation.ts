import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useContext } from 'react';
import { BifrostContext } from '../components/bifrost-provider';
import { executeGraphQL } from '../utils/graphql-client';

export interface UseBifrostMutationOptions {
  invalidateQueries?: string[];
  onSuccess?: (data: unknown) => void;
  onError?: (error: Error) => void;
}

export function useBifrostMutation<
  TData = unknown,
  TVariables extends Record<string, unknown> = Record<string, unknown>,
>(mutation: string, options: UseBifrostMutationOptions = {}) {
  const config = useContext(BifrostContext);
  if (!config) {
    throw new Error('useBifrostMutation must be used within a BifrostProvider');
  }

  const queryClient = useQueryClient();

  return useMutation<TData, Error, TVariables>({
    mutationFn: (variables) =>
      executeGraphQL<TData>(
        config.endpoint,
        config.headers ?? {},
        mutation,
        variables,
      ),
    onSuccess: (data) => {
      if (options.invalidateQueries) {
        for (const key of options.invalidateQueries) {
          queryClient.invalidateQueries({ queryKey: ['bifrost', key] });
        }
      }
      options.onSuccess?.(data);
    },
    onError: options.onError,
  });
}
