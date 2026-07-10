/**
 * Map a thrown error to a message safe to show the user. A `Failed to fetch`
 * (network/connection) error is rewritten into a plain-language hint that the
 * backend is unreachable; anything else surfaces its own message, falling back
 * to `fallback` when the value isn't an Error.
 */
export function toUserFacingError(err: unknown, fallback = 'An unexpected error occurred'): string {
  const msg = err instanceof Error ? err.message : fallback;
  return msg.includes('Failed to fetch')
    ? 'Cannot reach the backend server. It may have crashed or failed to start.'
    : msg;
}
