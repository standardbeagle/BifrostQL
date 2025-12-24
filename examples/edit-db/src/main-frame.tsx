import { useEffect } from 'react'
import { TableList } from './tableList';
import { DataPanel } from './data-panel';
import { Route, Routes, usePath } from './hooks/usePath';
import { Header } from './header';
import { DataEdit } from './data-edit';
import { ErrorBoundary } from './error-boundary';
import './main-frame.scss';

const PickATable = () => <p className="editdb-message">Select a Table</p>;
const Layout = () => <div className='editdb-frame-layout'>
    <div className="editdb-frame-layout-header">
        <ErrorBoundary section="Header">
            <Routes>
                <Route path='/:table/*' element={<Header />} />
                <Route path='/:table' element={<Header />} />
                <Route path='/' element={<Header />} />
            </Routes>
        </ErrorBoundary>
    </div>
    <div className="editdb-frame-layout-nav">
        <ErrorBoundary section="Table List">
            <TableList />
        </ErrorBoundary>
    </div>
    <div className="editdb-frame-layout-body">
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
    </div>
</div>

interface MainFrameProps {
    onLocate?: (location: string) => void;
}

export function MainFrame({ onLocate }: MainFrameProps) {
    const location = usePath();
    useEffect(() => { onLocate?.(location); }, [location, onLocate]);
    return <Layout/>;
}