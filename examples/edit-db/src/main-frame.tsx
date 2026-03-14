import { useState, useEffect } from 'react'
import { TableList } from './tableList';
import { DataPanel } from './data-panel';
import { Route, Routes, usePath } from './hooks/usePath';
import { Header } from './header';
import { DataEdit } from './data-edit';
import { ErrorBoundary } from './error-boundary';
import { Button } from '@/components/ui/button';
import { PanelLeft } from 'lucide-react';

const PickATable = () => <p className="p-5 text-center text-muted-foreground">Select a Table</p>;

function Layout() {
    const [sidebarOpen, setSidebarOpen] = useState(true);

    return (
        <div className={`grid min-h-screen grid-rows-[auto_1fr] ${sidebarOpen ? 'grid-cols-[minmax(150px,min-content)_1fr]' : 'grid-cols-[0px_1fr]'} md:grid-cols-[minmax(150px,min-content)_1fr] transition-[grid-template-columns] duration-200`}>
            <div className="col-span-full sticky top-0 z-50 bg-background border-b border-border flex items-center">
                <Button
                    variant="ghost"
                    size="icon"
                    className="md:hidden ml-1 shrink-0"
                    onClick={() => setSidebarOpen(!sidebarOpen)}
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
            <nav aria-label="Tables" className={`border-r border-border overflow-y-auto max-h-[calc(100vh-3.5rem)] sticky top-14 ${sidebarOpen ? '' : 'hidden'} md:block`}>
                <ErrorBoundary section="Table List">
                    <TableList />
                </ErrorBoundary>
            </nav>
            <main className="flex flex-col overflow-x-auto px-4 py-2">
                <ErrorBoundary section="Data Panel">
                    <Routes>
                        <Route path='/:table/from/:filterTable/:id/edit/:editid' element={<DataPanel />} />
                        <Route path='/:table/from/:filterTable/:id' element={<DataPanel />} />
                        <Route path='/:table/:id/edit/:editid' element={<DataPanel />} />
                        <Route path='/:table/:id' element={<DataPanel />} />
                        <Route path='/:table/edit/:editid' element={<DataPanel />} />
                        <Route path='/:table' element={<DataPanel />} />
                        <Route path='/' element={<PickATable />} />
                    </Routes>
                </ErrorBoundary>
                <ErrorBoundary section="Data Edit">
                    <Routes>
                        <Route path='/:table/from/:filterTable/:id/edit/:editid' element={<DataEdit />} />
                        <Route path='/:table/:id/edit/:editid' element={<DataEdit />} />
                        <Route path='/:table/edit/:editid' element={<DataEdit />} />
                        <Route path='/:table/edit' element={<DataEdit />} />
                    </Routes>
                </ErrorBoundary>
            </main>
        </div>
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
