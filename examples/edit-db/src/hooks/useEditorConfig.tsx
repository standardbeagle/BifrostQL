import { createContext, useContext, type ReactNode } from 'react';

/**
 * Optional, opt-in editor features. Kept tiny and separate from the data/schema
 * contexts so feature flags don't force those providers to re-render and so
 * TableList can read flags without prop-drilling through the router.
 */
export interface EditorConfig {
    /**
     * Show per-table statistics (row-count bars, column/FK counts) in the table
     * list. Off by default: it issues a row-count query per table, which a host
     * may not want on every connect. Hosts opt in via `<Editor showStats />`.
     */
    showStats: boolean;
    /**
     * Restrict the editor to these tables, by GraphQL name or db name. Undefined
     * exposes everything the server returns — which on a managed host also means
     * its own bookkeeping tables (Azure SQL surfaces firewall rules this way).
     * Filtering here rather than in the sidebar keeps excluded tables out of
     * routing and FK lookups too, not just out of the list.
     */
    tables?: string[];
    /**
     * Columns visible by default per table, keyed by table name; every other
     * column starts hidden. Only the starting point — a viewer's own choices are
     * stored per table and win from then on.
     */
    defaultColumns?: Record<string, string[]>;
    /**
     * Columns to move to the right end of every grid, in this order. For audit
     * stamps (created/modified) that are read far less often than the data they
     * annotate but sit in the middle of the natural column order.
     */
    trailingColumns?: string[];
}

const defaultConfig: EditorConfig = { showStats: false };

const EditorConfigContext = createContext<EditorConfig>(defaultConfig);

export function EditorConfigProvider({ config, children }: { config: EditorConfig; children: ReactNode }) {
    return <EditorConfigContext.Provider value={config}>{children}</EditorConfigContext.Provider>;
}

export function useEditorConfig(): EditorConfig {
    return useContext(EditorConfigContext);
}
