import { useState, useEffect, useRef } from 'react';
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
    const debounceRef = useRef<ReturnType<typeof setTimeout>>();

    const isNullOperator = operator === '_null';

    useEffect(() => {
        return () => {
            if (debounceRef.current) clearTimeout(debounceRef.current);
        };
    }, []);

    const applyFilter = (op: string, val: string) => {
        if (debounceRef.current) clearTimeout(debounceRef.current);

        if (op === '_null') {
            column.setFilterValue({ operator: '_null', value: true } as ColumnFilterValue);
            return;
        }

        if (!val) {
            column.setFilterValue(undefined);
            return;
        }

        debounceRef.current = setTimeout(() => {
            column.setFilterValue({ operator: op, value: val } as ColumnFilterValue);
        }, DEBOUNCE_MS);
    };

    const handleOperatorChange = (newOp: string) => {
        setOperator(newOp);
        if (newOp === '_null') {
            setInputValue('');
            column.setFilterValue({ operator: '_null', value: true } as ColumnFilterValue);
        } else if (inputValue) {
            applyFilter(newOp, inputValue);
        } else {
            column.setFilterValue(undefined);
        }
    };

    const handleInputChange = (val: string) => {
        setInputValue(val);
        applyFilter(operator, val);
    };

    const handleClear = () => {
        if (debounceRef.current) clearTimeout(debounceRef.current);
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
