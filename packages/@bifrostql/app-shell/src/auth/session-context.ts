import { createContext } from 'react';

/**
 * Client-side mirror of the BifrostQL `AppIdentity` contract.
 *
 * The C# source of truth is `src/BifrostQL.Core/Auth/AppIdentity.cs`, serialized
 * with camelCase property names. Collection properties (`orgIds`, `roles`,
 * `permissions`) are normalized server-side to non-null empty arrays, so they
 * are modeled here as required arrays rather than optionals. Scalar optionals
 * (`email`, `displayName`, `tenantId`) may be omitted on the wire.
 */
export interface AppIdentity {
  /** Stable, provider-neutral identifier for the authenticated user. */
  id: string;
  /** Name of the authentication provider (e.g. `local`, `oidc:google`). */
  provider: string;
  /** The user's email address, if known. */
  email?: string;
  /** Human-readable display name, if known. */
  displayName?: string;
  /** Primary tenant identifier for tenant isolation, if single-tenant. */
  tenantId?: string;
  /** All organization identifiers the user belongs to. Never null. */
  orgIds: string[];
  /** Roles granted to the user. Never null. */
  roles: string[];
  /** Fine-grained permissions granted to the user. Never null. */
  permissions: string[];
  /** Additional provider claims keyed by claim name. Never null. */
  claims: Record<string, unknown>;
}

/**
 * The resolved authentication session exposed to the app shell.
 *
 * Exactly one of the loading / authenticated / unauthenticated states is
 * active at a time, discriminated by `isLoading` and `isAuthenticated`.
 */
export interface SessionState {
  /** The authenticated identity, or `null` when unauthenticated or loading. */
  identity: AppIdentity | null;
  /** Convenience accessor for `identity.permissions`; `[]` when unauthenticated. */
  permissions: string[];
  /** Whether a valid authenticated identity is present. */
  isAuthenticated: boolean;
  /** Whether the session bootstrap request is still in flight. */
  isLoading: boolean;
  /**
   * The error from a session bootstrap request, if it failed for a reason
   * other than an expected `401` (which is treated as unauthenticated, not an
   * error). `null` while loading or on success.
   */
  error: Error | null;
  /** Re-run the session bootstrap request, bypassing the cache. */
  refresh: () => void;
}

/**
 * React context carrying the resolved {@link SessionState}. `null` when no
 * {@link SessionProvider} is mounted above the consumer; {@link useSession}
 * throws in that case.
 */
export const SessionContext = createContext<SessionState | null>(null);
