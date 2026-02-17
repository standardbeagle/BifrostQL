interface GraphQLResponse<T> {
  data?: T;
  errors?: Array<{ message: string }>;
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
 * Execute a GraphQL request against a BifrostQL endpoint via `fetch`.
 *
 * Handles bearer token injection, JSON serialization, and GraphQL error extraction.
 * Throws on HTTP errors, GraphQL errors, or empty responses.
 *
 * @typeParam T - The expected shape of the `data` field in the GraphQL response.
 * @param endpoint - The GraphQL endpoint URL.
 * @param headers - Static HTTP headers to include.
 * @param query - The GraphQL query or mutation string.
 * @param variables - Optional GraphQL variables.
 * @param signal - Optional `AbortSignal` for request cancellation.
 * @param getToken - Optional async/sync function returning a bearer token.
 * @returns The `data` field from the GraphQL response.
 * @throws {Error} On HTTP failure, GraphQL errors, or missing response data.
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
): Promise<T> {
  const body: Record<string, unknown> = { query };
  if (variables && Object.keys(variables).length > 0) {
    body.variables = variables;
  }

  const mergedHeaders: Record<string, string> = {
    'Content-Type': 'application/json',
    ...headers,
  };

  if (getToken) {
    const token = await getToken();
    if (token) {
      mergedHeaders['Authorization'] = `Bearer ${token}`;
    }
  }

  const response = await fetch(endpoint, {
    method: 'POST',
    headers: mergedHeaders,
    body: JSON.stringify(body),
    signal,
  });

  if (!response.ok) {
    throw new Error(
      `BifrostQL request failed: ${response.status} ${response.statusText}`,
    );
  }

  const json: GraphQLResponse<T> = await response.json();
  if (json.errors) {
    throw new Error(json.errors.map((e) => e.message).join(', '));
  }

  if (json.data === undefined) {
    throw new Error('BifrostQL response contained no data');
  }

  return json.data;
}
