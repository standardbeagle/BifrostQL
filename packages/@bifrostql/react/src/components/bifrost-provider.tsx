import { createContext, useContext, useMemo, useRef } from 'react';
import type { ReactNode } from 'react';
import {
  QueryClient,
  QueryClientContext,
  QueryClientProvider,
} from '@tanstack/react-query';
import type { BifrostConfig } from '../types';

export const BifrostContext = createContext<BifrostConfig | null>(null);

export interface BifrostProviderProps {
  config: BifrostConfig;
  queryClient?: QueryClient;
  children: ReactNode;
}

export function BifrostProvider({
  config,
  queryClient: externalClient,
  children,
}: BifrostProviderProps) {
  const parentClient = useContext(QueryClientContext);
  const internalClientRef = useRef<QueryClient | null>(null);

  const queryClient = useMemo(() => {
    if (externalClient) return externalClient;
    if (parentClient) return null;

    if (!internalClientRef.current) {
      const defaults = config.defaultQueryOptions;
      internalClientRef.current = new QueryClient({
        defaultOptions: {
          queries: {
            retry: defaults?.retry ?? 3,
            staleTime: defaults?.staleTime,
            gcTime: defaults?.gcTime,
          },
          mutations: {
            onError: config.onError,
          },
        },
      });
    }
    return internalClientRef.current;
  }, [externalClient, parentClient, config.defaultQueryOptions, config.onError]);

  const contextContent = (
    <BifrostContext.Provider value={config}>
      {children}
    </BifrostContext.Provider>
  );

  if (queryClient) {
    return (
      <QueryClientProvider client={queryClient}>
        {contextContent}
      </QueryClientProvider>
    );
  }

  return contextContent;
}
