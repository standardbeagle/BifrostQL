import { useState } from 'react';
import { Column } from '@tanstack/react-table';
import { X } from 'lucide-react';
import { Input } from '@/components/ui/input';
import { Button } from '@/components/ui/button';
import {
    Select,
    SelectContent,
    SelectItem,
    SelectTrigger,
    SelectValue,
} from '@/components/ui/select';
import type { ColumnFilterValue } from '@/hooks/useDataTable';
import { useDebouncedCommit } from './use-debounced-commit';

const operatorLabels: Record<string, string> = {
    _contains: 'Contains',
    _starts_with: 'Starts with',
    _ends_with: 'Ends with',
    _eq: 'Equals',
    _neq: 'Not equals',
    _null: 'Is null',
};

const DEBOUNCE_MS = 300;

interface TextFilterProps<TData, TValue> {
    column: Column<TData, TValue>;
}

export function TextFilter<TData, TValue>({ column }: TextFilterProps<TData, TValue>) {
    const operators = (column.columnDef.meta as { filterOperators?: string[] })?.filterOperators ?? [];
    const stringOperators = operators.filter((op) => op in operatorLabels);

    const currentFilter = column.getFilterValue() as ColumnFilterValue | undefined;
    const [operator, setOperator] = useState(currentFilter?.operator ?? '_contains');
    const [inputValue, setInputValue] = useState(
        currentFilter?.operator === '_null' ? '' : String(currentFilter?.value ?? '')
    );
    // Debounced apply with unmount flush, so a value typed just before the
    // menu closes (Radix unmounts the content) isn't silently dropped.
    const { schedule, cancel } = useDebouncedCommit(DEBOUNCE_MS);

    const isNullOperator = operator === '_null';

    const applyFilter = (op: string, val: string) => {
        if (op === '_null') {
            cancel();
            column.setFilterValue({ operator: '_null', value: true } as ColumnFilterValue);
            return;
        }

        if (!val) {
            cancel();
            column.setFilterValue(undefined);
            return;
        }

        schedule(() => column.setFilterValue({ operator: op, value: val } as ColumnFilterValue));
    };

    const handleOperatorChange = (newOp: string) => {
        setOperator(newOp);
        if (newOp === '_null') {
            setInputValue('');
            applyFilter('_null', '');
        } else if (inputValue) {
            applyFilter(newOp, inputValue);
        } else {
            cancel();
            column.setFilterValue(undefined);
        }
    };

    const handleInputChange = (val: string) => {
        setInputValue(val);
        applyFilter(operator, val);
    };

    const handleClear = () => {
        cancel();
        setOperator('_contains');
        setInputValue('');
        column.setFilterValue(undefined);
    };

    if (stringOperators.length === 0) return null;

    return (
        <div className="flex flex-col gap-2 p-2 min-w-[220px]">
            <Select value={operator} onValueChange={handleOperatorChange}>
                <SelectTrigger size="sm" className="w-full text-xs">
                    <SelectValue />
                </SelectTrigger>
                <SelectContent>
                    {stringOperators.map((op) => (
                        <SelectItem key={op} value={op}>
                            {operatorLabels[op]}
                        </SelectItem>
                    ))}
                </SelectContent>
            </Select>
            {!isNullOperator && (
                <Input
                    placeholder="Filter value..."
                    value={inputValue}
                    onChange={(e) => handleInputChange(e.target.value)}
                    className="h-8 text-xs"
                    autoFocus
                />
            )}
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
