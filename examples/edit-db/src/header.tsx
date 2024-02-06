import React, { useEffect, useMemo, useState } from 'react';
import { useSchema } from './hooks/useData';
import { useHistory, useNavigate, useNavigation, useParams } from './hooks/usePath';

export function Header() {
    const tableData = useParams();
    const { loading, error, data: schema } = useSchema();
    const [searchVal, setSearchVal] = useState("");
    const navigate = useNavigate();
    const { back, hasBack } = useNavigation();
    //console.log(schema, tableData);
    const tableName = tableData?.table;
    const tableSchema = useMemo(() => schema?.find((t: any) => t.graphQlName === tableName), [schema, tableName]);
    const options = tableSchema?.columns
                        ?.map((c: any) => ({ key: c.name, value: `${c.name},${c.paramType}`, label: c.label }));
    const [column, setColumn] = useState(options?.at(0)?.value ?? "");
    //The control needs to reset state when a new table is selected, ie the filter is cleared
    useEffect(() => { setColumn(options?.at(0)?.value ?? ""); }, [tableData])
    const filter = () => {
        if (!searchVal) return;
        const [columnName, type] = column.split(",");
        if (type === "Int")
            navigate(`?filter=["${columnName}", "_eq", ${searchVal}, "${type}"]`);
        if (type === "String")
            navigate(`?filter=["${columnName}", "_eq", "${searchVal}", "${type}"]`);
    }
    if (loading) return <div>Loading...</div>;
    if (error) return <div>Error: {error.message}</div>;
    return (
        <header className="editdb-header">
            <div>
                {hasBack && <button onClick={() => back()}>‹</button>}
                {!hasBack && <span>‹</span>}
            </div>
            <h3>Table: { tableSchema?.dbName ?? tableData?.table ?? "(Select)"}</h3>
            {options && <>
                <select onChange={e => setColumn(e.target.value)}>
                    {options.map((c: any) => (<option key={c.key} value={c.value}>{c.label}</option>))}
                </select>
                <input type="search" value={searchVal} onChange={(event) => setSearchVal(event.target.value)}></input>
                <button onClick={filter}>filter</button>
                <button onClick={() => navigate("/")}>clear</button>
                { tableSchema?.isEditable &&  <button onClick={() => navigate(`edit/`)}>add</button>}
            </>}
        </header>
    )
}