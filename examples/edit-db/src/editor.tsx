import { useMemo } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MainFrame } from './main-frame';
import { PathProvider } from './hooks/usePath';
import { SchemaProvider } from './hooks/useSchema';
import { GraphQLFetcher, HttpGraphQLFetcher, FetcherProvider } from './common/fetcher';
import { EditorConfigProvider } from './hooks/useEditorConfig';
import { ToastProvider } from './hooks/useToast';
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
    /**
     * Show per-table statistics (row-count bars, column/FK counts) in the table
     * list. Off by default — it issues a row-count query per table. Defaults to false.
     */
    showStats?: boolean;
    /**
     * Restrict the editor to these tables, by GraphQL name or db name. Omit to
     * expose every table the server returns, including any bookkeeping tables a
     * managed host adds to the database.
     */
    tables?: string[];
    /**
     * Columns visible by default per table, keyed by table name (`{ workshops:
     * ['id', 'number'] }`). Every other column of that table starts hidden.
     * A viewer's own column choices are stored per table and take over from there.
     */
    defaultColumns?: Record<string, string[]>;
    /** Columns to move to the right end of every grid, in this order (e.g. audit stamps). */
    trailingColumns?: string[];
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
    showStats = false,
    tables,
    defaultColumns,
    trailingColumns,
}: EditorProps) {
    const resolvedFetcher = useMemo(() => {
        if (fetcher) return fetcher;
        if (!uri) return null;
        return new HttpGraphQLFetcher(uri);
    }, [uri, fetcher]);

    // Memoized: the config context feeds the schema filter and every grid, so a
    // fresh object each render would re-run those consumers for nothing.
    const config = useMemo(
        () => ({ showStats, tables, defaultColumns, trailingColumns }),
        [showStats, tables, defaultColumns, trailingColumns],
    );

    const queryClient = useMemo(() => new QueryClient({
        defaultOptions: {
            queries: {
                staleTime: 5 * 60 * 1000,
                retry: 1,
            },
        },
    }), []);

    if (!resolvedFetcher) {
        throw new Error(
            'Editor requires either a `fetcher` (GraphQLFetcher) or a `uri` prop to reach the GraphQL endpoint; neither was provided.',
        );
    }

    return (
        <QueryClientProvider client={queryClient}>
            <FetcherProvider value={resolvedFetcher}>
                <EditorConfigProvider config={config}>
                    <ToastProvider>
                        <PathProvider path={uiPath || "/"}>
                            <SchemaProvider>
                                <MainFrame onLocate={onLocate} />
                            </SchemaProvider>
                        </PathProvider>
                    </ToastProvider>
                </EditorConfigProvider>
            </FetcherProvider>
        </QueryClientProvider>
    )
}
