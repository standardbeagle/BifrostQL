import { createContext, useContext } from 'react';

export interface GraphQLError {
    message: string;
    locations?: { line: number; column: number }[];
    path?: (string | number)[];
    extensions?: Record<string, unknown>;
}

export class GraphQLRequestError extends Error {
    constructor(
        public readonly errors: GraphQLError[],
        public readonly data?: unknown
    ) {
        super(errors.map(e => e.message).join('; '));
        this.name = 'GraphQLRequestError';
    }
}

export interface GraphQLFetcher {
    query<T = unknown>(query: string, variables?: Record<string, unknown>): Promise<T>;
}

export class HttpGraphQLFetcher implements GraphQLFetcher {
    constructor(private readonly uri: string) {}

    async query<T = unknown>(query: string, variables?: Record<string, unknown>): Promise<T> {
        const response = await fetch(this.uri, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ query, variables }),
        });

        if (!response.ok) {
            throw new Error(`GraphQL request failed: ${response.status} ${response.statusText}`);
        }

        const result = await response.json();

        if (result.errors?.length) {
            throw new GraphQLRequestError(result.errors, result.data);
        }

        return result.data as T;
    }
}

const FetcherContext = createContext<GraphQLFetcher | null>(null);

export const FetcherProvider = FetcherContext.Provider;

export function useFetcher(): GraphQLFetcher {
    const fetcher = useContext(FetcherContext);
    if (!fetcher) throw new Error('useFetcher must be used within a FetcherProvider');
    return fetcher;
}
