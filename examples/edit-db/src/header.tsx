import { useEffect, useMemo, useState } from 'react';
import { useSchema } from './hooks/useSchema';
import { useNavigate, useNavigation, useParams } from './hooks/usePath';
import { Table, Column } from './types/schema';

interface HeaderRouteParams {
    table?: string;
    [key: string]: string | undefined;
}

interface ColumnOption {
    key: string;
    value: string;
    label: string;
}

export function Header() {
    const tableData = useParams<HeaderRouteParams>();
    const { loading, error, data: schema } = useSchema();
    const [searchVal, setSearchVal] = useState("");
    const navigate = useNavigate();
    const { back, hasBack } = useNavigation();
    const tableName = tableData?.table;
    const tableSchema = useMemo(() => schema?.find((t: Table) => t.graphQlName === tableName), [schema, tableName]);
    const options: ColumnOption[] | undefined = useMemo(
        () => tableSchema?.columns?.map((c: Column) => ({ key: c.name, value: `${c.name},${c.paramType}`, label: c.label })),
        [tableSchema]
    );
    const [column, setColumn] = useState(options?.at(0)?.value ?? "");
    // Reset state when a new table is selected (filter is cleared)
    useEffect(() => {
        setSearchVal("");
        setColumn(options?.at(0)?.value ?? "");
    }, [options]);
    const filter = () => {
        if (!searchVal) return;
        const [columnName, type] = column.split(",");
        if (type === "Int" || type === "Int!" || type === "Float" || type === "Float!")
            navigate(`?filter=["${columnName}", "_eq", ${searchVal}, "${type}"]`);
        if (type === "String" || type === "String!")
            navigate(`?filter=["${columnName}", "_contains", "${searchVal}", "${type}"]`);
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
                    {options.map((c: ColumnOption) => (<option key={c.key} value={c.value}>{c.label}</option>))}
                </select>
                <input type="search" value={searchVal} onChange={(event) => setSearchVal(event.target.value)}></input>
                <button onClick={filter}>filter</button>
                <button onClick={() => navigate("/")}>clear</button>
                { tableSchema?.isEditable &&  <button onClick={() => navigate(`edit/`)}>add</button>}
            </>}
        </header>
    )
}