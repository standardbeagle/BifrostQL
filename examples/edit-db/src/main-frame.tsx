import { useState, useEffect } from 'react'
import { TableList } from './table-list';
import { DataPanel } from './data-panel';
import { Route, Routes, usePath } from './hooks/usePath';
import { Header } from './header';
import { DataEdit } from './data-edit';
import { ErrorBoundary } from './error-boundary';
import { ColumnNavProvider } from './hooks/useColumnNav';
import { usePanelSize } from './hooks/usePanelSize';
import { Button } from '@/components/ui/button';
import { PanelLeft, Database, Table2, MousePointerClick, Loader2 } from 'lucide-react';
import { useSchema } from './hooks/useSchema';
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert';
import { AlertCircle } from 'lucide-react';

function StartPage() {
    const { loading, error, data } = useSchema();

    if (loading) {
        return (
            <div className="flex flex-col items-center justify-center h-full gap-3 text-muted-foreground">
                <Database className="size-10 animate-pulse" />
                <p className="text-sm">Loading schema...</p>
            </div>
        );
    }

    if (error) {
        return (
            <div className="flex flex-col items-center justify-center h-full gap-4 max-w-md mx-auto text-center">
                <Database className="size-12 text-destructive" />
                <div>
                    <h2 className="text-lg font-semibold text-foreground">Connection Error</h2>
                    <p className="mt-1 text-sm text-muted-foreground">{error.message}</p>
                </div>
            </div>
        );
    }

    if (data.length === 0) {
        return (
            <div className="flex flex-col items-center justify-center h-full gap-4 max-w-md mx-auto text-center">
                <Database className="size-12 text-muted-foreground/50" />
                <div>
                    <h2 className="text-lg font-semibold text-foreground">No Tables Found</h2>
                    <p className="mt-1 text-sm text-muted-foreground">
                        This database doesn't contain any tables. It may be a new or empty database.
                    </p>
                    <p className="mt-3 text-xs text-muted-foreground/70">
                        Create tables in your database and reconnect, or try a different connection.
                    </p>
                </div>
            </div>
        );
    }

    return (
        <div className="flex flex-col items-center justify-center h-full gap-6 max-w-lg mx-auto text-center">
            <div className="flex items-center gap-3 text-muted-foreground/50">
                <Table2 className="size-10" />
                <MousePointerClick className="size-6" />
            </div>
            <div>
                <h2 className="text-lg font-semibold text-foreground">Select a Table</h2>
                <p className="mt-1 text-sm text-muted-foreground">
                    Choose a table from the sidebar to browse and edit its data.
                </p>
            </div>
            <div className="text-xs text-muted-foreground/60 border border-border rounded-md px-4 py-3 bg-muted/30">
                <p className="font-medium text-muted-foreground mb-1">{data.length} table{data.length !== 1 ? 's' : ''} available</p>
                <p className="leading-relaxed">
                    {data.slice(0, 8).map(t => t.label).join(', ')}
                    {data.length > 8 ? `, and ${data.length - 8} more` : ''}
                </p>
            </div>
        </div>
    );
}

// Gate that checks schema loading state before rendering children.
// Catches connection errors once here instead of showing duplicate
// error panels in Header, TableList, DataPanel, and DataEdit.
function SchemaGate({ children }: { children: React.ReactNode }) {
    const { loading, error } = useSchema();

    if (loading) {
        return (
            <div className="flex items-center justify-center flex-1 gap-3 text-muted-foreground">
                <Loader2 className="size-6 animate-spin" />
                <span className="text-sm">Connecting to database...</span>
            </div>
        );
    }

    if (error) {
        return (
            <div className="flex items-center justify-center flex-1">
                <Alert variant="destructive" className="max-w-lg">
                    <AlertCircle className="size-4" />
                    <AlertTitle>Database Connection Error</AlertTitle>
                    <AlertDescription className="mt-2">
                        <p className="text-sm">{error.message}</p>
                        <p className="text-xs text-muted-foreground mt-2">
                            Check your connection string and ensure the database server is running.
                        </p>
                    </AlertDescription>
                </Alert>
            </div>
        );
    }

    return <>{children}</>;
}

function Layout() {
    const [sidebarOpen, setSidebarOpen] = useState(true);
    const sidebar = usePanelSize({ key: 'sidebar-width', initial: 240, min: 150, max: 560, axis: 'x' });

    return (
        <SchemaGate>
        <ColumnNavProvider>
            <div
                className="grid h-full flex-1 min-h-0 grid-rows-[auto_1fr]"
                style={{ gridTemplateColumns: sidebarOpen ? `${sidebar.size}px 1fr` : '0px 1fr' }}
            >
                <div className="col-span-full sticky top-0 z-50 bg-background border-b border-border flex items-center">
                    <Button
                        variant="ghost"
                        size="icon"
                        className="md:hidden ml-1 shrink-0"
                        onClick={() => setSidebarOpen(!sidebarOpen)}
                        aria-label="Toggle sidebar"
                        title="Toggle sidebar"
                    >
                        <PanelLeft className="size-5" />
                    </Button>
                    <div className="flex-1 min-w-0">
                        <ErrorBoundary section="Header">
                            <Routes>
                                <Route path='/:table/*' element={<Header />} />
                                <Route path='/:table' element={<Header />} />
                                <Route path='/' element={<Header />} />
                            </Routes>
                        </ErrorBoundary>
                    </div>
                </div>
                <nav aria-label="Tables" className={`relative border-r border-border overflow-hidden min-h-0 ${sidebarOpen ? '' : 'hidden'} md:block`}>
                    <ErrorBoundary section="Table List">
                        <TableList />
                    </ErrorBoundary>
                    <div
                        role="separator"
                        aria-orientation="vertical"
                        aria-label="Resize table list"
                        tabIndex={0}
                        data-testid="sidebar-resize-handle"
                        className="absolute inset-y-0 right-0 w-1.5 cursor-col-resize select-none touch-none hover:bg-primary/40 focus-visible:bg-primary/60 focus-visible:outline-none"
                        onPointerDown={sidebar.onPointerDown}
                        onKeyDown={sidebar.onKeyDown}
                    />
                </nav>
                <main className="flex flex-col overflow-hidden min-h-0 px-1 py-0.5">
                    <ErrorBoundary section="Data Panel">
                        <Routes>
                            <Route path='/:table/from/:filterTable/:id/edit/:editId' element={<DataPanel />} />
                            <Route path='/:table/from/:filterTable/:id' element={<DataPanel />} />
                            <Route path='/:table/:id/edit/:editId' element={<DataPanel />} />
                            <Route path='/:table/:id' element={<DataPanel />} />
                            <Route path='/:table/edit/:editId' element={<DataPanel />} />
                            {/* Bare create route: keeps the grid mounted behind the New-record
                                dialog and stops `:id` from capturing the "edit" keyword
                                (which fired a bogus get-by-id with $id="edit"). */}
                            <Route path='/:table/edit' element={<DataPanel />} />
                            <Route path='/:table' element={<DataPanel />} />
                            <Route path='/' element={<StartPage />} />
                        </Routes>
                    </ErrorBoundary>
                    <ErrorBoundary section="Data Edit">
                        <Routes>
                            <Route path='/:table/from/:filterTable/:id/edit/:editId' element={<DataEdit />} />
                            <Route path='/:table/:id/edit/:editId' element={<DataEdit />} />
                            <Route path='/:table/edit/:editId' element={<DataEdit />} />
                            <Route path='/:table/edit' element={<DataEdit />} />
                        </Routes>
                    </ErrorBoundary>
                </main>
            </div>
        </ColumnNavProvider>
        </SchemaGate>
    );
}

interface MainFrameProps {
    onLocate?: (location: string) => void;
}

export function MainFrame({ onLocate }: MainFrameProps) {
    const location = usePath();
    useEffect(() => { onLocate?.(location); }, [location, onLocate]);
    return <Layout/>;
}
