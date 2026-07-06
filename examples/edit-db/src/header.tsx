import { useEffect, useMemo, useState } from 'react';
import { useSchema } from './hooks/useSchema';
import { useColumnNav } from './hooks/useColumnNav';
import { useNavigate, useNavigation, useParams, useSearchParams } from './hooks/usePath';
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
import { ChevronLeft, Filter, Plus, Search, X, Loader2, Home } from 'lucide-react';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { humanizeName } from './lib/humanize';

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
    const { mainTable, columns, focusedIndex, focusColumn, closeColumn } = useColumnNav();
    const [searchVal, setSearchVal] = useState("");
    const navigate = useNavigate();
    const { search } = useSearchParams();
    const hasFilter = !!search.get('filter');
    const { back, hasBack } = useNavigation();
    const tableName = tableData?.table;
    const hasOpenColumns = columns.length > 0;
    const tableSchema = useMemo(() => schema?.find((t: Table) => t.graphQlName === tableName), [schema, tableName]);
    // Quick search only supports the text/number operators built below, so limit
    // the column picker to String/Int/Float. DateTime and Boolean still filter via
    // the per-column grid header filters; offering them here would silently no-op.
    const options: ColumnOption[] | undefined = useMemo(
        () => tableSchema?.columns
            ?.filter((c: Column) => {
                const base = c.paramType.replace('!', '');
                return base === 'String' || base === 'Int' || base === 'Float';
            })
            .map((c: Column) => ({ key: c.name, value: `${c.name},${c.paramType}`, label: c.label })),
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
        // No column selected (e.g. table has no searchable column) — nothing to do.
        if (!type) return;
        const base = type.replace("!", "");
        if (base === "Int" || base === "Float") {
            // Guard against interpolating non-numeric input straight into the
            // filter JSON (which produced a malformed param).
            const n = Number(searchVal);
            if (!Number.isFinite(n)) return;
            navigate(`?filter=["${columnName}", "_eq", ${n}, "${type}"]`);
        } else if (base === "String") {
            // JSON.stringify escapes quotes/backslashes so a search term can't
            // break out of the filter string.
            navigate(`?filter=["${columnName}", "_contains", ${JSON.stringify(searchVal)}, "${type}"]`);
        }
    }
    // Clear only the header filter param, preserving the current table/route and
    // any column filters (cf). The adjacent "Clear" button below goes home instead.
    const clearFilter = () => {
        setSearchVal("");
        const params = new URLSearchParams(search.toString());
        params.delete('filter');
        const qs = params.toString();
        navigate(qs ? `?${qs}` : '?');
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
        <header className="flex flex-wrap items-center gap-x-3 gap-y-1.5 px-3 py-1.5 min-h-[2.5rem]">
            <div className="flex items-center gap-1.5 overflow-x-auto min-w-0 flex-1">
                <Button
                    variant="ghost"
                    size="icon-sm"
                    onClick={() => hasBack && back()}
                    disabled={!hasBack}
                    aria-label="Go back"
                    title="Go back"
                    className="shrink-0"
                >
                    <ChevronLeft className="size-4" />
                </Button>
                {hasOpenColumns ? (
                    <div className="flex items-center gap-1 overflow-x-auto">
                        <button
                            type="button"
                            onClick={() => focusColumn(0)}
                            className={`flex items-center gap-1 px-2 py-0.5 rounded text-xs font-medium whitespace-nowrap transition-colors ${
                                focusedIndex === 0
                                    ? 'bg-primary text-primary-foreground'
                                    : 'bg-muted/50 text-muted-foreground hover:bg-muted'
                            }`}
                        >
                            {tableSchema?.label ?? humanizeName(tableData?.table ?? '')}
                        </button>
                        {columns.map((col, i) => {
                            const colTable = schema?.find((t: Table) => t.graphQlName === col.panel.tableName);
                            const colLabel = colTable?.label ?? humanizeName(col.panel.tableName);
                            return (
                                <span
                                    key={`${col.panel.tableName}-${col.panel.filterId ?? i}`}
                                    className={`inline-flex items-center gap-1 pl-2 pr-1 py-0.5 rounded text-xs font-medium whitespace-nowrap transition-colors ${
                                        focusedIndex === i + 1
                                            ? 'bg-primary text-primary-foreground'
                                            : 'bg-muted/50 text-muted-foreground hover:bg-muted'
                                    }`}
                                >
                                    <button
                                        type="button"
                                        onClick={() => focusColumn(i + 1)}
                                        className="whitespace-nowrap"
                                    >
                                        {colLabel}
                                    </button>
                                    <button
                                        type="button"
                                        onClick={(e) => { e.stopPropagation(); closeColumn(i + 1); }}
                                        className="rounded-sm hover:bg-background/50 p-px"
                                        aria-label={`Close ${colLabel}`}
                                    >
                                        <X className="size-3" />
                                    </button>
                                </span>
                            );
                        })}
                    </div>
                ) : (
                    <h2 className="text-sm font-semibold whitespace-nowrap">
                        {tableSchema?.label ?? tableData?.table ?? "(Select)"}
                    </h2>
                )}
            </div>
            {options && options.length > 0 && (
                <div className="flex items-center border border-border rounded-md overflow-hidden flex-[2_2_16rem] min-w-[12rem]">
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
                    {hasFilter && (
                        <Button
                            variant="ghost"
                            size="sm"
                            onClick={clearFilter}
                            title="Clear filter"
                            aria-label="Clear filter"
                            className="shrink-0 rounded-none border-l border-border h-8 px-2 text-xs text-muted-foreground"
                        >
                            <X className="size-3.5" />
                        </Button>
                    )}
                </div>
            )}
            {tableSchema && (
                <div className="flex items-center gap-px border border-border rounded-md overflow-hidden shrink-0">
                    <Button variant="ghost" size="sm" onClick={() => navigate("/")} title="Back to table list" className="text-muted-foreground text-xs h-8 px-3 rounded-none">
                        <Home className="size-3.5" />
                        Home
                    </Button>
                    {tableSchema?.isEditable && (
                        <Button variant="default" size="sm" onClick={() => navigate(`edit/`)} className="h-8 text-xs px-4 rounded-none">
                            <Plus className="size-3.5" />
                            Add
                        </Button>
                    )}
                </div>
            )}
        </header>
    )
}
