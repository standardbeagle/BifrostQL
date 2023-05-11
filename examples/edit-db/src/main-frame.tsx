import React, { useEffect } from 'react'
import { TableList } from './tableList';
import { DataPanel } from './data-panel';
import { Route, Routes, usePath } from './hooks/usePath';
import { Header } from './header';
import { DataEdit } from './data-edit';
import './main-frame.scss';

const PickATable = () => <p className="editdb-message">Select a Table</p>;
const Layout = () => <div className='editdb-frame-layout'>
    <div className="editdb-frame-layout-header">
        <Routes>
            <Route path='/:table/*' element={<Header />} />
            <Route path='/:table' element={<Header />} />
            <Route path='/' element={<Header />} />
        </Routes>
    </div>
    <div className="editdb-frame-layout-nav"><TableList /></div>
    <div className="editdb-frame-layout-body">
        <Routes>
            <Route path='/:table/from/:filterTable/:id/edit/:editid' element={<DataPanel />} />
            <Route path='/:table/from/:filterTable/:id' element={<DataPanel />} />
            <Route path='/:table/:id/edit/:editid' element={<DataPanel />} />
            <Route path='/:table/:id' element={<DataPanel />} />
            <Route path='/:table/edit/:editid' element={<DataPanel />} />
            <Route path='/:table' element={<DataPanel />} />
            <Route path='/' element={<PickATable />} />
        </Routes>
        <Routes>
            <Route path='/:table/from/:filterTable/:id/edit/:editid' element={<DataEdit />} />
            <Route path='/:table/:id/edit/:editid' element={<DataEdit />} />
            <Route path='/:table/edit/:editid' element={<DataEdit />} />
            <Route path='/:table/edit' element={<DataEdit />} />
        </Routes>
    </div>
</div>

export function MainFrame({ onLocate }: any) {
    const location = usePath();
    useEffect(() => { onLocate && onLocate(location); }, [location]);
    return <Layout/>;
}