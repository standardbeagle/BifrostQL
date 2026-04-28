import { useMemo } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MainFrame } from './main-frame';
import { PathProvider } from './hooks/usePath';
import { SchemaProvider } from './hooks/useSchema';
import { GraphQLFetcher, HttpGraphQLFetcher, FetcherProvider } from './common/fetcher';
import './globals.css';

/**
 * Props for the Editor component.
 * @interface EditorProps
 */
interface EditorProps {
    /** GraphQL endpoint URL. Either uri or fetcher is required. */
    uri?: string;
    /** Base path for client-side routing. Defaults to '/' */
    uiPath?: string;
    /** Custom GraphQL fetcher implementation for advanced use cases */
    fetcher?: GraphQLFetcher;
    /** Callback invoked when navigation occurs */
    onLocate?: (location: string) => void;
}

/**
 * Editor component - Root component for the database administration interface.
 * 
 * Sets up React Query, schema context, and client-side routing. Connects to
 * a GraphQL API (typically BifrostQL) to automatically generate forms and
 * data tables based on database schema introspection.
 * 
 * @example
 * ```tsx
 * <Editor uri="/graphql" uiPath="/admin" />
 * ```
 * 
 * @example
 * ```tsx
 * <Editor 
 *   fetcher={customFetcher} 
 *   uiPath="/admin"
 *   onLocate={(path) => console.log('Navigated to:', path)}
 * />
 * ```
 * 
 * @param props - Editor configuration props
 * @returns React element containing the full editor interface
 */
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
                        <MainFrame onLocate={onLocate} />
                    </SchemaProvider>
                </PathProvider>
            </FetcherProvider>
        </QueryClientProvider>
    )
}
