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
import { ChevronLeft, Filter, Plus, Search, X, Loader2 } from 'lucide-react';
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
        <header className="grid grid-cols-[auto_1fr_auto] items-center gap-3 px-3 py-1.5 min-h-[2.5rem]">
            <div className="flex items-center gap-1.5">
                <Button
                    variant="ghost"
                    size="icon-sm"
                    onClick={() => hasBack && back()}
                    disabled={!hasBack}
                    aria-label="Go back"
                    title="Go back"
                >
                    <ChevronLeft className="size-4" />
                </Button>
                <h2 className="text-sm font-semibold whitespace-nowrap">
                    {tableSchema?.dbName ?? tableData?.table ?? "(Select)"}
                </h2>
            </div>
            {options ? <>
                <div className="flex items-center border border-border rounded-md overflow-hidden">
                    <Select value={column} onValueChange={setColumn}>
                        <SelectTrigger size="sm" className="w-auto min-w-[120px] shrink-0 border-0 rounded-none border-r border-border h-8 text-xs">
                            <SelectValue />
                        </SelectTrigger>
                        <SelectContent>
                            {options.map((c: ColumnOption) => (
                                <SelectItem key={c.key} value={c.value}>{c.label}</SelectItem>
                            ))}
                        </SelectContent>
                    </Select>
                    <div className="relative flex-1 min-w-[180px]">
                        <Search className="absolute left-3 top-1/2 -translate-y-1/2 size-3.5 text-muted-foreground pointer-events-none" />
                        <Input
                            type="search"
                            value={searchVal}
                            onChange={(event) => setSearchVal(event.target.value)}
                            onKeyDown={(e) => e.key === 'Enter' && filter()}
                            className="h-8 border-0 rounded-none focus-visible:ring-0 focus-visible:ring-offset-0"
                            style={{ paddingLeft: '2.25rem' }}
                            placeholder="Search..."
                        />
                    </div>
                    <Button variant="ghost" size="sm" onClick={filter} className="shrink-0 rounded-none border-l border-border h-8 px-3 text-xs">
                        <Filter className="size-3.5" />
                        Filter
                    </Button>
                </div>
                <div className="flex items-center gap-px border border-border rounded-md overflow-hidden">
                    <Button variant="ghost" size="sm" onClick={() => navigate("/")} className="text-muted-foreground text-xs h-8 px-3 rounded-none">
                        <X className="size-3.5" />
                        Clear
                    </Button>
                    {tableSchema?.isEditable && (
                        <Button variant="default" size="sm" onClick={() => navigate(`edit/`)} className="h-8 text-xs px-4 rounded-none">
                            <Plus className="size-3.5" />
                            Add
                        </Button>
                    )}
                </div>
            </> : <div className="col-span-2" />}
        </header>
    )
}
