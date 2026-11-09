/** One entry of a GraphQL response's `errors` list, as the server sent it. */
export interface GraphQLErrorEntry {
  message: string;
  locations?: ReadonlyArray<{ line: number; column: number }>;
  path?: ReadonlyArray<string | number>;
  extensions?: { code?: string; [key: string]: unknown };
}

interface GraphQLResponse<T> {
  data?: T;
  errors?: GraphQLErrorEntry[];
}

/**
 * Typed failure surfaced when a request fails authentication (HTTP `401` or a
 * GraphQL auth error) and the failure could not be recovered — either no
 * `refreshToken` hook was configured, the refresh attempt threw, or the single
 * retry still failed authentication.
 *
 * Consumers can use `instanceof BifrostAuthError` to distinguish an
 * unrecoverable session failure from ordinary request/GraphQL errors.
 */
export class BifrostAuthError extends Error {
  /** The underlying error that triggered the auth failure, when available. */
  readonly cause?: unknown;

  constructor(message: string, cause?: unknown) {
    super(message);
    this.name = 'BifrostAuthError';
    this.cause = cause;
  }
}

/**
 * Typed failure surfaced when the server answers with a GraphQL `errors` list
 * that is not an authentication failure. The message is every error's message,
 * joined.
 *
 * Use `code` to tell conditions apart (`CONFLICT`, `ACCESS_DENIED`, an
 * application's own code) instead of matching message text.
 */
export class BifrostGraphQLError extends Error {
  /** `extensions.code` of the first error that has one. */
  readonly code?: string;
  /** Every error the server sent, unchanged. */
  readonly errors: readonly GraphQLErrorEntry[];

  constructor(errors: readonly GraphQLErrorEntry[]) {
    super(errors.map((error) => error.message).join(', '));
    this.name = 'BifrostGraphQLError';
    this.code = errors.find((error) => error.extensions?.code)?.extensions
      ?.code;
    this.errors = errors;
  }
}

/**
 * Optional auth-recovery hooks passed to {@link executeGraphQL}. These mirror
 * the `refreshToken` / `onSessionExpired` fields on `BifrostConfig` and are
 * forwarded by the hook layer.
 */
export interface BifrostAuthHandlers {
  /**
   * Invoked on an auth failure to obtain a fresh credential. May return a new
   * bearer token string, or `null`/`undefined` to indicate the token store was
   * refreshed out-of-band (the retry then re-reads via `getToken`).
   */
  refreshToken?: () => string | null | void | Promise<string | null | void>;
  /** Invoked with the typed error when an auth failure could not be recovered. */
  onSessionExpired?: (error: Error) => void;
}

/**
 * `extensions.code` values that mean the caller is not authenticated (HTTP 401
 * semantics), which trigger the token refresh + session-expiry flow. The engine
 * emits neither: its auth failures are an HTTP 401 on the mount. They are the
 * convention for gateways and server operations.
 *
 * Authorization codes (`FORBIDDEN`, `ACCESS_DENIED`) are deliberately excluded:
 * the credential is valid but lacks permission, so refreshing the token and
 * expiring the session is wrong — it would loop or log the user out over a mere
 * permission denial.
 */
const AUTH_ERROR_CODES: ReadonlySet<string> = new Set([
  'UNAUTHENTICATED',
  'UNAUTHORIZED',
]);

/** The same decision for an error with no code, read from its message. */
const AUTH_ERROR_PATTERN = /\b(unauthorized|unauthenticated)\b/i;

/**
 * A code decides when the server sent one, so a refusal whose message happens to
 * say "unauthorized" stays the refusal it is.
 */
function isAuthError(error: GraphQLErrorEntry): boolean {
  const code = error.extensions?.code;
  return code
    ? AUTH_ERROR_CODES.has(code)
    : AUTH_ERROR_PATTERN.test(error.message);
}

/**
 * Compute an exponential backoff delay for retry attempts, capped at 30 seconds.
 *
 * @param attempt - Zero-based retry attempt number.
 * @param baseDelay - Base delay in milliseconds (doubled each attempt).
 * @returns Delay in milliseconds.
 */
export function defaultRetryDelay(attempt: number, baseDelay: number): number {
  return Math.min(baseDelay * 2 ** attempt, 30_000);
}

/**
 * Resolve the bearer token to use for a request, preferring an explicit token
 * (e.g. one just returned by `refreshToken`) over the `getToken` provider.
 */
async function resolveToken(
  getToken?: () => string | null | Promise<string | null>,
  explicitToken?: string | null,
): Promise<string | null> {
  if (explicitToken != null && explicitToken !== '') {
    return explicitToken;
  }
  if (getToken) {
    return await getToken();
  }
  return null;
}

/** Build the request headers, injecting the bearer token when present. */
function buildHeaders(
  headers: Record<string, string>,
  token: string | null,
): Record<string, string> {
  const merged: Record<string, string> = {
    'Content-Type': 'application/json',
    ...headers,
  };
  if (token) {
    merged['Authorization'] = `Bearer ${token}`;
  }
  return merged;
}

/**
 * Send one GraphQL request and parse the response.
 *
 * @returns The `data` field on success.
 * @throws {BifrostAuthError} On HTTP `401` or a GraphQL auth error.
 * @throws {BifrostGraphQLError} On any other GraphQL errors.
 * @throws {Error} On other HTTP failures or missing data.
 */
async function sendRequest<T>(
  endpoint: string,
  headers: Record<string, string>,
  body: string,
  signal?: AbortSignal,
): Promise<T> {
  const response = await fetch(endpoint, {
    method: 'POST',
    headers,
    body,
    signal,
  });

  if (response.status === 401) {
    throw new BifrostAuthError(
      `BifrostQL request failed: 401 ${response.statusText}`,
    );
  }

  if (!response.ok) {
    throw new Error(
      `BifrostQL request failed: ${response.status} ${response.statusText}`,
    );
  }

  const json: GraphQLResponse<T> = await response.json();
  if (json.errors) {
    const error = new BifrostGraphQLError(json.errors);
    if (json.errors.some(isAuthError)) {
      throw new BifrostAuthError(error.message);
    }
    throw error;
  }

  if (json.data === undefined) {
    throw new Error('BifrostQL response contained no data');
  }

  return json.data;
}

/**
 * Execute a GraphQL request against a BifrostQL endpoint via `fetch`.
 *
 * Handles bearer token injection, JSON serialization, and GraphQL error
 * extraction. On an authentication failure (HTTP `401` or a GraphQL auth
 * error), an optional `refreshToken` hook is invoked, after which the request
 * is retried exactly once with a refreshed credential. If no `refreshToken`
 * hook is configured, the refresh throws, or the single retry still fails
 * authentication, a {@link BifrostAuthError} is thrown and the optional
 * `onSessionExpired` hook is invoked with it.
 *
 * The transport path uses only `fetch`, `AbortSignal`, and `JSON` — all
 * available in React Native — so it is RN-compatible. See the React Native
 * feasibility guide in the docs for known gaps.
 *
 * @typeParam T - The expected shape of the `data` field in the GraphQL response.
 * @param endpoint - The GraphQL endpoint URL.
 * @param headers - Static HTTP headers to include.
 * @param query - The GraphQL query or mutation string.
 * @param variables - Optional GraphQL variables.
 * @param signal - Optional `AbortSignal` for request cancellation.
 * @param getToken - Optional async/sync function returning a bearer token.
 * @param auth - Optional auth-recovery hooks (`refreshToken` / `onSessionExpired`).
 * @returns The `data` field from the GraphQL response.
 * @throws {BifrostAuthError} On an unrecoverable authentication failure.
 * @throws {BifrostGraphQLError} On non-auth GraphQL errors, carrying `code` and `errors`.
 * @throws {Error} On HTTP failure or missing response data.
 *
 * @example
 * ```ts
 * const data = await executeGraphQL<{ users: User[] }>(
 *   'https://api.example.com/graphql',
 *   {},
 *   '{ users { id name } }',
 * );
 * ```
 */
export async function executeGraphQL<T>(
  endpoint: string,
  headers: Record<string, string>,
  query: string,
  variables?: Record<string, unknown>,
  signal?: AbortSignal,
  getToken?: () => string | null | Promise<string | null>,
  auth?: BifrostAuthHandlers,
): Promise<T> {
  const requestBody: Record<string, unknown> = { query };
  if (variables && Object.keys(variables).length > 0) {
    requestBody.variables = variables;
  }
  const body = JSON.stringify(requestBody);

  const token = await resolveToken(getToken);

  try {
    return await sendRequest<T>(
      endpoint,
      buildHeaders(headers, token),
      body,
      signal,
    );
  } catch (error) {
    if (!(error instanceof BifrostAuthError)) {
      throw error;
    }

    if (!auth?.refreshToken) {
      auth?.onSessionExpired?.(error);
      throw error;
    }

    let refreshedToken: string | null;
    try {
      const refreshResult = await auth.refreshToken();
      refreshedToken = await resolveToken(
        getToken,
        typeof refreshResult === 'string' ? refreshResult : null,
      );
    } catch (refreshError) {
      const failure = new BifrostAuthError(
        'BifrostQL session refresh failed',
        refreshError,
      );
      auth.onSessionExpired?.(failure);
      throw failure;
    }

    try {
      return await sendRequest<T>(
        endpoint,
        buildHeaders(headers, refreshedToken),
        body,
        signal,
      );
    } catch (retryError) {
      if (retryError instanceof BifrostAuthError) {
        auth.onSessionExpired?.(retryError);
      }
      throw retryError;
    }
  }
}
