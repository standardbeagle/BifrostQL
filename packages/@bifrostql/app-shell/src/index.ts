export { useAppMetadata } from './metadata/use-app-metadata';
export type { UseAppMetadataResult } from './metadata/use-app-metadata';
export type {
  AppMetadata,
  EntityMetadata,
  FieldMetadata,
  GridMetadata,
  SavedViewMetadata,
  RelationshipMetadata,
  RelationshipKind,
} from './metadata/types';

export { SessionProvider } from './auth/session-provider';
export type { SessionProviderProps } from './auth/session-provider';
export { useSession } from './auth/use-session';
export { SessionContext } from './auth/session-context';
export type { AppIdentity, SessionState } from './auth/session-context';

export { AppShellProvider } from './app-shell-provider';
export type {
  AppShellProviderProps,
  AppShellConfig,
} from './app-shell-provider';
