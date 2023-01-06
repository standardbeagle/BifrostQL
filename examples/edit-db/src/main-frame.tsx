import React, { useEffect } from 'react'
import { TableList } from './tableList';
import { DataPanel } from './data-panel';
import './index.css';
import { Route, Routes, usePath } from './hooks/usePath';
import { Header } from './header';

const PickATable = () => <p className="editdb-message">Select a Table</p>;
const Layout = () => <div className='editdb-frame-layout'>
    <div className="editdb-frame-layout-header">
        <Routes>
            <Route path='/:table' element={<Header />} />
            <Route path='/*' element={<Header />} />
        </Routes>
    </div>
    <div className="editdb-frame-layout-nav"><TableList /></div>
    <div className="editdb-frame-layout-body">
        <Routes>
            <Route path='/:table' element={<DataPanel />} />
            <Route path='/*' element={<PickATable />} />
        </Routes>
    </div>
</div>

export function MainFrame({ onLocate }: any) {
    const location = usePath();
    useEffect(() => { onLocate && onLocate(location); }, [location]);
    return <Layout/>;
}