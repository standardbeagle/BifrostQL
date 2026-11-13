import { createContext, useContext, useState } from 'react';
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

function createInternalClient(config: BifrostConfig): QueryClient {
  const defaults = config.defaultQueryOptions;
  return new QueryClient({
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

export function BifrostProvider({
  config,
  queryClient: externalClient,
  children,
}: BifrostProviderProps) {
  const parentClient = useContext(QueryClientContext);
  const needsInternalClient = !externalClient && !parentClient;

  // The internal client is created once, the first time neither a
  // `queryClient` prop nor a parent QueryClientProvider is available, and
  // then kept for the provider's lifetime so its cache survives re-renders.
  const [internalClient, setInternalClient] = useState<QueryClient | null>(
    () => (needsInternalClient ? createInternalClient(config) : null),
  );
  if (needsInternalClient && !internalClient) {
    setInternalClient(createInternalClient(config));
  }

  const queryClient = externalClient ?? (parentClient ? null : internalClient);

  const contextContent = (
    <BifrostContext.Provider value={config}>{children}</BifrostContext.Provider>
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
