import { useContext, useState } from 'react';
import { BifrostContext } from '@bifrostql/react';
import { useSession } from '@bifrostql/app-shell';
import { Onboarding } from './onboarding';

/**
 * Conventional path of the local-auth login endpoint, a same-origin sibling of
 * the GraphQL endpoint — mirrors how the app-shell `SessionProvider` derives
 * `/auth/session`. A successful `POST` sets the auth cookie; the SPA then
 * refetches the session so the app gates open.
 */
const LOGIN_PATH = '/auth/login';

/**
 * Conventional path of the read-session endpoint — the same one the app-shell
 * `SessionProvider` polls. The login screen reads it directly once, before
 * declaring success, because `useSession().refresh()` returns `void` and so
 * cannot report that the session failed to open.
 */
const SESSION_PATH = '/auth/session';

/**
 * Derive an `/auth/*` endpoint URL from the configured BifrostQL GraphQL
 * endpoint. In hosted mode the auth routes are same-origin siblings of the
 * GraphQL endpoint, so the path segment is replaced rather than appended.
 *
 * @param graphqlEndpoint - The configured GraphQL endpoint URL.
 * @param path - The absolute auth path to target (e.g. `/auth/login`).
 * @returns The absolute auth endpoint URL.
 */
function resolveAuthUrl(graphqlEndpoint: string, path: string): string {
  const url = new URL(graphqlEndpoint);
  url.pathname = path;
  url.search = '';
  url.hash = '';
  return url.toString();
}

/**
 * Login screen for the Membership Manager SPA.
 *
 * Rendered by the `/login` route, which the app's {@link ProtectedRoute} gates
 * redirect to when the session is unauthenticated. Submitting the form posts
 * the username/password to `/auth/login` with `credentials: 'include'` so the
 * host can issue its auth cookie. A 200 from `/auth/login` is not on its own
 * proof that the operator is signed in — the cookie can still be rejected
 * (cross-origin, `SameSite`) or the read-session endpoint can fail — so the
 * screen reads `/auth/session` itself before declaring success, and only then
 * refreshes the shared session via {@link useSession} to flip the app's gates.
 * A failed login, or a login whose session never opens, surfaces inline and
 * re-enables the form; the submit button stays disabled until the session has
 * actually resolved, so the form cannot be double-submitted in the gap.
 *
 * The screen also composes the {@link Onboarding} panel so a first-run operator
 * sees the "sign in with the seeded admin credentials" guidance alongside the
 * form. Must be mounted within an `AppShellProvider`.
 */
export function Login() {
  const config = useContext(BifrostContext);
  if (!config) {
    throw new Error('Login must be used within a BifrostProvider');
  }

  const { refresh } = useSession();
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  const handleSubmit = async (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    setError(null);
    setSubmitting(true);
    try {
      const response = await fetch(
        resolveAuthUrl(config.endpoint, LOGIN_PATH),
        {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          credentials: 'include',
          body: JSON.stringify({ username, password }),
        },
      );

      if (!response.ok) {
        const body = (await response.json().catch(() => null)) as {
          error?: string;
        } | null;
        setError(body?.error ?? 'Sign in failed. Check your credentials.');
        setSubmitting(false);
        return;
      }

      // The cookie is issued — but only a session read proves it was accepted.
      const sessionResponse = await fetch(
        resolveAuthUrl(config.endpoint, SESSION_PATH),
        { credentials: 'include' },
      );
      const identity = sessionResponse.ok
        ? ((await sessionResponse.json().catch(() => null)) as unknown)
        : null;
      if (identity === null) {
        setError(
          'Signed in, but the session could not be established. Check that cookies are allowed for this site, then try again.',
        );
        setSubmitting(false);
        return;
      }

      // The session is real: drop the password and refresh the shared session
      // so the app's gates re-evaluate. `submitting` stays set — the gates
      // replace this screen, and until they do the form must not re-submit.
      setPassword('');
      refresh();
    } catch {
      setError('Could not reach the server. Try again.');
      setSubmitting(false);
    }
  };

  return (
    <section data-testid="login-screen" className="login-screen">
      <h1>Sign in to Membership Manager</h1>
      <form data-testid="login-form" onSubmit={handleSubmit}>
        <div>
          <label htmlFor="login-username">Username</label>
          <input
            id="login-username"
            name="username"
            value={username}
            onChange={(e) => setUsername(e.target.value)}
            autoComplete="username"
          />
        </div>
        <div>
          <label htmlFor="login-password">Password</label>
          <input
            id="login-password"
            name="password"
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete="current-password"
          />
        </div>
        {error ? (
          <p role="alert" data-testid="login-error">
            {error}
          </p>
        ) : null}
        <button type="submit" disabled={submitting}>
          {submitting ? 'Signing in…' : 'Sign in'}
        </button>
      </form>
      <Onboarding />
    </section>
  );
}
