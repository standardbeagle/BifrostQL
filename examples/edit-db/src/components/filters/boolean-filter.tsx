import { Column } from '@tanstack/react-table';
import { X } from 'lucide-react';
import { Button } from '@/components/ui/button';
import {
    Select,
    SelectContent,
    SelectItem,
    SelectTrigger,
    SelectValue,
} from '@/components/ui/select';
import type { ColumnFilterValue } from '@/hooks/useDataTable';

type FilterOption = 'all' | 'true' | 'false' | '_null';

const optionLabels: Record<FilterOption, string> = {
    all: 'All',
    true: 'True',
    false: 'False',
    _null: 'Is null',
};

function getCurrentOption(filter: ColumnFilterValue | undefined): FilterOption {
    if (!filter) return 'all';
    if (filter.operator === '_null') return '_null';
    if (filter.operator === '_eq' && filter.value === true) return 'true';
    if (filter.operator === '_eq' && filter.value === false) return 'false';
    return 'all';
}

interface BooleanFilterProps<TData, TValue> {
    column: Column<TData, TValue>;
}

export function BooleanFilter<TData, TValue>({ column }: BooleanFilterProps<TData, TValue>) {
    const operators = (column.columnDef.meta as { filterOperators?: string[] })?.filterOperators ?? [];
    const hasNull = operators.includes('_null');

    const currentFilter = column.getFilterValue() as ColumnFilterValue | undefined;
    const selected = getCurrentOption(currentFilter);

    const options: FilterOption[] = hasNull
        ? ['all', 'true', 'false', '_null']
        : ['all', 'true', 'false'];

    const handleChange = (value: FilterOption) => {
        switch (value) {
            case 'true':
                column.setFilterValue({ operator: '_eq', value: true } as ColumnFilterValue);
                break;
            case 'false':
                column.setFilterValue({ operator: '_eq', value: false } as ColumnFilterValue);
                break;
            case '_null':
                column.setFilterValue({ operator: '_null', value: true } as ColumnFilterValue);
                break;
            default:
                column.setFilterValue(undefined);
        }
    };

    const handleClear = () => {
        column.setFilterValue(undefined);
    };

    return (
        <div className="flex flex-col gap-2 p-2 min-w-[220px]">
            <Select value={selected} onValueChange={handleChange}>
                <SelectTrigger size="sm" className="w-full text-xs">
                    <SelectValue />
                </SelectTrigger>
                <SelectContent>
                    {options.map((opt) => (
                        <SelectItem key={opt} value={opt}>
                            {optionLabels[opt]}
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
