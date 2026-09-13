import { useContext } from 'react';
import { usePolicy as useServerPolicy } from '@bifrostql/react';
import type { UsePolicyResult } from '@bifrostql/react';
import { SessionContext } from './session-context';

/**
 * `@bifrostql/react`'s `usePolicy`, keyed on the app shell's session.
 *
 * The policy answer is cached per identity, and the identity here is the
 * session's `identity.id` — not a request header, which is empty for the
 * cookie session and the `getToken` flow alike. Signing out, signing in, or
 * switching users therefore fetches the projection again instead of serving
 * the previous user's grants. The query waits until the session bootstrap has
 * resolved so it is not first asked as anonymous and then again as the user.
 *
 * Outside a `SessionProvider` the answer is the anonymous caller's.
 *
 * @param tableGraphQlName - GraphQL table name as `_dbSchema(graphQlName:)`
 *   accepts it. Omit to load grants only.
 */
export function usePolicy(tableGraphQlName?: string): UsePolicyResult {
  const session = useContext(SessionContext);
  return useServerPolicy(tableGraphQlName, {
    identity: session?.identity?.id,
    enabled: !session?.isLoading,
  });
}
