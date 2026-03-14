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

const operatorLabels: Record<string, string> = {
    _eq: 'On',
    _neq: 'Not on',
    _gt: 'After',
    _gte: 'On or after',
    _lt: 'Before',
    _lte: 'On or before',
    _between: 'Between',
    _null: 'Is null',
};

interface DateFilterProps<TData, TValue> {
    column: Column<TData, TValue>;
}

export function DateFilter<TData, TValue>({ column }: DateFilterProps<TData, TValue>) {
    const operators = (column.columnDef.meta as { filterOperators?: string[] })?.filterOperators ?? [];
    const dateOperators = operators.filter((op) => op in operatorLabels);

    const currentFilter = column.getFilterValue() as ColumnFilterValue | undefined;

    const initialOperator = currentFilter?.operator ?? '_eq';
    const [operator, setOperator] = useState(initialOperator);

    const [inputValue, setInputValue] = useState(() => {
        if (!currentFilter || currentFilter.operator === '_null' || currentFilter.operator === '_between') return '';
        return String(currentFilter.value ?? '');
    });
    const [fromValue, setFromValue] = useState(() => {
        if (currentFilter?.operator === '_between' && Array.isArray(currentFilter.value)) return String(currentFilter.value[0] ?? '');
        return '';
    });
    const [toValue, setToValue] = useState(() => {
        if (currentFilter?.operator === '_between' && Array.isArray(currentFilter.value)) return String(currentFilter.value[1] ?? '');
        return '';
    });

    const applyFilter = (op: string, val: string) => {
        if (op === '_null') {
            column.setFilterValue({ operator: '_null', value: true } as ColumnFilterValue);
            return;
        }
        if (!val) {
            column.setFilterValue(undefined);
            return;
        }
        column.setFilterValue({ operator: op, value: val } as ColumnFilterValue);
    };

    const applyBetweenFilter = (from: string, to: string) => {
        if (!from || !to) {
            column.setFilterValue(undefined);
            return;
        }
        column.setFilterValue({ operator: '_between', value: [from, to] } as ColumnFilterValue);
    };

    const handleOperatorChange = (newOp: string) => {
        setOperator(newOp);
        if (newOp === '_null') {
            setInputValue('');
            setFromValue('');
            setToValue('');
            column.setFilterValue({ operator: '_null', value: true } as ColumnFilterValue);
        } else if (newOp === '_between') {
            setInputValue('');
            if (fromValue && toValue) {
                applyBetweenFilter(fromValue, toValue);
            } else {
                column.setFilterValue(undefined);
            }
        } else {
            setFromValue('');
            setToValue('');
            if (inputValue) {
                applyFilter(newOp, inputValue);
            } else {
                column.setFilterValue(undefined);
            }
        }
    };

    const handleInputChange = (val: string) => {
        setInputValue(val);
        applyFilter(operator, val);
    };

    const handleFromChange = (val: string) => {
        setFromValue(val);
        applyBetweenFilter(val, toValue);
    };

    const handleToChange = (val: string) => {
        setToValue(val);
        applyBetweenFilter(fromValue, val);
    };

    const handleClear = () => {
        setOperator('_eq');
        setInputValue('');
        setFromValue('');
        setToValue('');
        column.setFilterValue(undefined);
    };

    if (dateOperators.length === 0) return null;

    const isBetween = operator === '_between';
    const isNull = operator === '_null';

    return (
        <div className="flex flex-col gap-2 p-2 min-w-[220px]">
            <Select value={operator} onValueChange={handleOperatorChange}>
                <SelectTrigger size="sm" className="w-full text-xs">
                    <SelectValue />
                </SelectTrigger>
                <SelectContent>
                    {dateOperators.map((op) => (
                        <SelectItem key={op} value={op}>
                            {operatorLabels[op]}
                        </SelectItem>
                    ))}
                </SelectContent>
            </Select>
            {!isNull && !isBetween && (
                <Input
                    type="date"
                    value={inputValue}
                    onChange={(e) => handleInputChange(e.target.value)}
                    className="h-8 text-xs"
                />
            )}
            {isBetween && (
                <div className="flex gap-2">
                    <Input
                        type="date"
                        value={fromValue}
                        onChange={(e) => handleFromChange(e.target.value)}
                        className="h-8 text-xs"
                    />
                    <Input
                        type="date"
                        value={toValue}
                        onChange={(e) => handleToChange(e.target.value)}
                        className="h-8 text-xs"
                    />
                </div>
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
