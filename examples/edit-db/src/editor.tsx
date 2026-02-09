import { useMemo } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MainFrame } from './main-frame';
import { PathProvider } from './hooks/usePath';
import { SchemaProvider } from './hooks/useSchema';
import { GraphQLFetcher, HttpGraphQLFetcher, FetcherProvider } from './common/fetcher';

interface EditorProps {
    uri?: string;
    uiPath?: string;
    fetcher?: GraphQLFetcher;
    onLocate?: (location: string) => void;
}

export function Editor({
    uri,
    fetcher,
    uiPath,
    onLocate,
}: EditorProps) {
    const resolvedFetcher = useMemo(() => {
        if (fetcher) return fetcher;
        if (!uri) return null;
        return new HttpGraphQLFetcher(uri);
    }, [uri, fetcher]);

    const queryClient = useMemo(() => new QueryClient({
        defaultOptions: {
            queries: {
                staleTime: 5 * 60 * 1000,
                retry: 1,
            },
        },
    }), []);

    if (!resolvedFetcher) return <section>CONFIG MISSING...</section>;

    return (
        <QueryClientProvider client={queryClient}>
            <FetcherProvider value={resolvedFetcher}>
                <PathProvider path={uiPath || "/"}>
                    <SchemaProvider>
                        <div><MainFrame onLocate={onLocate} /></div>
                    </SchemaProvider>
                </PathProvider>
            </FetcherProvider>
        </QueryClientProvider>
    )
}
