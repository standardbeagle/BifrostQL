import { useEffect, useMemo, useState } from 'react';
import { useSchema } from './hooks/useSchema';
import { useNavigate, useNavigation, useParams } from './hooks/usePath';
import { Table, Column } from './types/schema';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import {
    Select,
    SelectContent,
    SelectItem,
    SelectTrigger,
    SelectValue,
} from '@/components/ui/select';
import { Loader2 } from 'lucide-react';
import { Alert, AlertDescription } from '@/components/ui/alert';

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
    if (loading) return (
        <div className="flex items-center gap-2 p-2.5">
            <Loader2 className="size-4 animate-spin text-muted-foreground" />
            <span className="text-sm text-muted-foreground">Loading...</span>
        </div>
    );
    if (error) return (
        <Alert variant="destructive" className="m-2">
            <AlertDescription>Error: {error.message}</AlertDescription>
        </Alert>
    );
    return (
        <header className="flex flex-wrap items-center gap-2 p-2.5">
            <Button
                variant="ghost"
                size="sm"
                onClick={() => hasBack && back()}
                disabled={!hasBack}
                className="px-2"
            >
                &lsaquo;
            </Button>
            <h3 className="text-sm font-semibold whitespace-nowrap">
                Table: {tableSchema?.dbName ?? tableData?.table ?? "(Select)"}
            </h3>
            {options && <>
                <Select value={column} onValueChange={setColumn}>
                    <SelectTrigger size="sm" className="w-auto min-w-[100px]">
                        <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                        {options.map((c: ColumnOption) => (
                            <SelectItem key={c.key} value={c.value}>{c.label}</SelectItem>
                        ))}
                    </SelectContent>
                </Select>
                <Input
                    type="search"
                    value={searchVal}
                    onChange={(event) => setSearchVal(event.target.value)}
                    onKeyDown={(e) => e.key === 'Enter' && filter()}
                    className="min-w-[120px] max-w-[300px] flex-1 h-8"
                    placeholder="Search..."
                />
                <Button variant="secondary" size="sm" onClick={filter}>Filter</Button>
                <Button variant="outline" size="sm" onClick={() => navigate("/")}>Clear</Button>
                {tableSchema?.isEditable && (
                    <Button variant="default" size="sm" onClick={() => navigate(`edit/`)}>Add</Button>
                )}
            </>}
        </header>
    )
}
