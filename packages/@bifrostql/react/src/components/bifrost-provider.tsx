import { createContext } from 'react';
import type { ReactNode } from 'react';
import type { BifrostConfig } from '../types';

export const BifrostContext = createContext<BifrostConfig | null>(null);

interface BifrostProviderProps {
  config: BifrostConfig;
  children: ReactNode;
}

export function BifrostProvider({ config, children }: BifrostProviderProps) {
  return (
    <BifrostContext.Provider value={config}>{children}</BifrostContext.Provider>
  );
}
