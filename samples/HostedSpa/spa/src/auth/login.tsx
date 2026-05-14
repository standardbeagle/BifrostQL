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
 * host can issue its auth cookie; on success the session is refreshed via
 * {@link useSession}, which flips the app's gates to the authenticated app. A
 * failed login surfaces the server's error message inline.
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
        return;
      }

      // Cookie issued — refresh the session so the app's gates re-evaluate.
      refresh();
    } catch {
      setError('Could not reach the server. Try again.');
    } finally {
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
