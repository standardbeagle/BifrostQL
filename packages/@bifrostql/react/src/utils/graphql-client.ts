interface GraphQLResponse<T> {
  data?: T;
  errors?: Array<{ message: string }>;
}

export async function executeGraphQL<T>(
  endpoint: string,
  headers: Record<string, string>,
  query: string,
  variables?: Record<string, unknown>,
  signal?: AbortSignal,
): Promise<T> {
  const body: Record<string, unknown> = { query };
  if (variables && Object.keys(variables).length > 0) {
    body.variables = variables;
  }

  const response = await fetch(endpoint, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      ...headers,
    },
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
