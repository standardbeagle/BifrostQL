import { Column } from '@tanstack/react-table';
import { useQuery } from '@tanstack/react-query';
import { X } from 'lucide-react';
import { Button } from '@/components/ui/button';
import {
    Select,
    SelectContent,
    SelectItem,
    SelectTrigger,
    SelectValue,
} from '@/components/ui/select';
import { useFetcher } from '@/common/fetcher';
import { useSchema } from '@/hooks/useSchema';
import type { ColumnFilterValue } from '@/hooks/useDataTable';

interface LookupRow {
    id: number | string;
    label: string;
}

interface LookupResponse {
    [tableName: string]: {
        data: LookupRow[];
    };
}

interface FkFilterProps<TData, TValue> {
    column: Column<TData, TValue>;
    joinTable: string;
    joinLabelColumn: string;
    /**
     * The destination column of the join (FK target). Used to project the option values
     * so the filter applies against the same column the FK points at. Do NOT substitute
     * the target table's first primary-key column here — on composite-PK or denormalized-FK
     * targets those are different columns.
     */
    joinFkColumn: string;
}

export function FkFilter<TData, TValue>({ column, joinTable, joinLabelColumn, joinFkColumn }: FkFilterProps<TData, TValue>) {
    const fetcher = useFetcher();
    const schema = useSchema();
    const joinSchema = schema.findTable(joinTable);

    const query = `query Lookup${joinTable} { ${joinTable}(sort: [${joinLabelColumn}_asc], limit: 100) { data { id: ${joinFkColumn} label: ${joinLabelColumn} } } }`;

    const { data: lookupData } = useQuery({
        queryKey: ['fkLookup', joinTable, joinLabelColumn],
        queryFn: () => fetcher.query<LookupResponse>(query),
        staleTime: 5 * 60 * 1000,
        enabled: !!joinSchema,
    });

    const options = lookupData?.[joinTable]?.data ?? [];
    const currentFilter = column.getFilterValue() as ColumnFilterValue | undefined;

    const selectedValue = currentFilter?.operator === '_eq' && currentFilter.value != null
        ? String(currentFilter.value)
        : 'all';

    const handleChange = (value: string) => {
        if (value === 'all') {
            column.setFilterValue(undefined);
            return;
        }
        const numValue = Number(value);
        const filterValue = Number.isNaN(numValue) ? value : numValue;
        column.setFilterValue({ operator: '_eq', value: filterValue } as ColumnFilterValue);
    };

    const handleClear = () => {
        column.setFilterValue(undefined);
    };

    return (
        <div className="flex flex-col gap-2 p-2 min-w-[220px]">
            <Select value={selectedValue} onValueChange={handleChange}>
                <SelectTrigger size="sm" className="w-full text-xs">
                    <SelectValue />
                </SelectTrigger>
                <SelectContent>
                    <SelectItem value="all">All</SelectItem>
                    {options.map((opt) => (
                        <SelectItem key={String(opt.id)} value={String(opt.id)}>
                            {String(opt.label)}
                        </SelectItem>
                    ))}
                </SelectContent>
            </Select>
            {(currentFilter !== undefined) && (
                <Button
                    variant="ghost"
                    size="sm"
                    className="h-7 text-xs justify-start"
                    onClick={handleClear}
                >
                    <X className="size-3" />
                    Clear filter
                </Button>
            )}
        </div>
    );
}
