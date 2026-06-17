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
}

const defaultConfig: EditorConfig = { showStats: false };

const EditorConfigContext = createContext<EditorConfig>(defaultConfig);

export function EditorConfigProvider({ config, children }: { config: EditorConfig; children: ReactNode }) {
    return <EditorConfigContext.Provider value={config}>{children}</EditorConfigContext.Provider>;
}

export function useEditorConfig(): EditorConfig {
    return useContext(EditorConfigContext);
}
