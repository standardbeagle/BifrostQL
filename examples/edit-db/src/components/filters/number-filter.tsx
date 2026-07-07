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
    _eq: 'Equals',
    _neq: 'Not equals',
    _gt: 'Greater than',
    _gte: 'Greater or equal',
    _lt: 'Less than',
    _lte: 'Less or equal',
    _between: 'Between',
    _null: 'Is null',
};

const DEBOUNCE_MS = 300;

interface NumberFilterProps<TData, TValue> {
    column: Column<TData, TValue>;
}

function isIntColumn<TData, TValue>(column: Column<TData, TValue>): boolean {
    const paramType = (column.columnDef.meta as { paramType?: string })?.paramType ?? '';
    return paramType.replace('!', '') === 'Int';
}

export function parseNumeric(raw: string, isInt: boolean): number | null {
    const text = raw.trim();
    if (text === '' || text === '-' || text === '+') return null;
    const valid = isInt
        ? /^[+-]?\d+$/.test(text)
        : /^[+-]?(?:(?:\d+\.?\d*)|(?:\.\d+))(?:[eE][+-]?\d+)?$/.test(text);
    if (!valid) return null;
    const n = Number(text);
    return Number.isFinite(n) ? n : null;
}

export function NumberFilter<TData, TValue>({ column }: NumberFilterProps<TData, TValue>) {
    const operators = (column.columnDef.meta as { filterOperators?: string[] })?.filterOperators ?? [];
    const numericOperators = operators.filter((op) => op in operatorLabels);
    const isInt = isIntColumn(column);

    const currentFilter = column.getFilterValue() as ColumnFilterValue | undefined;

    const initialOperator = currentFilter?.operator ?? '_eq';
    const [operator, setOperator] = useState(initialOperator);

    const [inputValue, setInputValue] = useState(() => {
        if (!currentFilter || currentFilter.operator === '_null' || currentFilter.operator === '_between') return '';
        return String(currentFilter.value ?? '');
    });
    const [minValue, setMinValue] = useState(() => {
        if (currentFilter?.operator === '_between' && Array.isArray(currentFilter.value)) return String(currentFilter.value[0] ?? '');
        return '';
    });
    const [maxValue, setMaxValue] = useState(() => {
        if (currentFilter?.operator === '_between' && Array.isArray(currentFilter.value)) return String(currentFilter.value[1] ?? '');
        return '';
    });

    // Debounced apply with unmount flush, so a value typed just before the
    // menu closes isn't dropped when Radix unmounts the dropdown content.
    const { schedule, cancel } = useDebouncedCommit(DEBOUNCE_MS);

    const applyFilter = (op: string, val: string) => {
        if (op === '_null') {
            cancel();
            column.setFilterValue({ operator: '_null', value: true } as ColumnFilterValue);
            return;
        }

        const parsed = parseNumeric(val, isInt);
        if (parsed === null) {
            cancel();
            column.setFilterValue(undefined);
            return;
        }

        schedule(() => column.setFilterValue({ operator: op, value: parsed } as ColumnFilterValue));
    };

    const applyBetweenFilter = (min: string, max: string) => {
        const parsedMin = parseNumeric(min, isInt);
        const parsedMax = parseNumeric(max, isInt);

        if (parsedMin === null || parsedMax === null) {
            cancel();
            column.setFilterValue(undefined);
            return;
        }

        schedule(() => column.setFilterValue({ operator: '_between', value: [parsedMin, parsedMax] } as ColumnFilterValue));
    };

    const handleOperatorChange = (newOp: string) => {
        setOperator(newOp);
        if (newOp === '_null') {
            setInputValue('');
            setMinValue('');
            setMaxValue('');
            applyFilter('_null', '');
        } else if (newOp === '_between') {
            setInputValue('');
            if (minValue && maxValue) {
                applyBetweenFilter(minValue, maxValue);
            } else {
                cancel();
                column.setFilterValue(undefined);
            }
        } else {
            setMinValue('');
            setMaxValue('');
            if (inputValue) {
                applyFilter(newOp, inputValue);
            } else {
                cancel();
                column.setFilterValue(undefined);
            }
        }
    };

    const handleInputChange = (val: string) => {
        setInputValue(val);
        applyFilter(operator, val);
    };

    const handleMinChange = (val: string) => {
        setMinValue(val);
        applyBetweenFilter(val, maxValue);
    };

    const handleMaxChange = (val: string) => {
        setMaxValue(val);
        applyBetweenFilter(minValue, val);
    };

    const handleClear = () => {
        cancel();
        setOperator('_eq');
        setInputValue('');
        setMinValue('');
        setMaxValue('');
        column.setFilterValue(undefined);
    };

    if (numericOperators.length === 0) return null;

    const isBetween = operator === '_between';
    const isNull = operator === '_null';

    return (
        <div className="flex flex-col gap-2 p-2 min-w-[220px]">
            <Select value={operator} onValueChange={handleOperatorChange}>
                <SelectTrigger size="sm" className="w-full text-xs">
                    <SelectValue />
                </SelectTrigger>
                <SelectContent>
                    {numericOperators.map((op) => (
                        <SelectItem key={op} value={op}>
                            {operatorLabels[op]}
                        </SelectItem>
                    ))}
                </SelectContent>
            </Select>
            {!isNull && !isBetween && (
                <Input
                    type="number"
                    placeholder="Filter value..."
                    value={inputValue}
                    onChange={(e) => handleInputChange(e.target.value)}
                    className="h-8 text-xs"
                    autoFocus
                />
            )}
            {isBetween && (
                <div className="flex gap-2">
                    <Input
                        type="number"
                        placeholder="Min"
                        value={minValue}
                        onChange={(e) => handleMinChange(e.target.value)}
                        className="h-8 text-xs"
                        autoFocus
                    />
                    <Input
                        type="number"
                        placeholder="Max"
                        value={maxValue}
                        onChange={(e) => handleMaxChange(e.target.value)}
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
