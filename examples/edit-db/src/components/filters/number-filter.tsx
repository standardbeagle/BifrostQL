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

function parseNumeric(raw: string, isInt: boolean): number | null {
    if (raw === '' || raw === '-') return null;
    const n = isInt ? parseInt(raw, 10) : parseFloat(raw);
    return Number.isNaN(n) ? null : n;
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

    const debounceRef = useRef<ReturnType<typeof setTimeout>>();

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

        const parsed = parseNumeric(val, isInt);
        if (parsed === null) {
            column.setFilterValue(undefined);
            return;
        }

        debounceRef.current = setTimeout(() => {
            column.setFilterValue({ operator: op, value: parsed } as ColumnFilterValue);
        }, DEBOUNCE_MS);
    };

    const applyBetweenFilter = (min: string, max: string) => {
        if (debounceRef.current) clearTimeout(debounceRef.current);

        const parsedMin = parseNumeric(min, isInt);
        const parsedMax = parseNumeric(max, isInt);

        if (parsedMin === null || parsedMax === null) {
            column.setFilterValue(undefined);
            return;
        }

        debounceRef.current = setTimeout(() => {
            column.setFilterValue({ operator: '_between', value: [parsedMin, parsedMax] } as ColumnFilterValue);
        }, DEBOUNCE_MS);
    };

    const handleOperatorChange = (newOp: string) => {
        setOperator(newOp);
        if (newOp === '_null') {
            setInputValue('');
            setMinValue('');
            setMaxValue('');
            column.setFilterValue({ operator: '_null', value: true } as ColumnFilterValue);
        } else if (newOp === '_between') {
            setInputValue('');
            if (minValue && maxValue) {
                applyBetweenFilter(minValue, maxValue);
            } else {
                column.setFilterValue(undefined);
            }
        } else {
            setMinValue('');
            setMaxValue('');
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

    const handleMinChange = (val: string) => {
        setMinValue(val);
        applyBetweenFilter(val, maxValue);
    };

    const handleMaxChange = (val: string) => {
        setMaxValue(val);
        applyBetweenFilter(minValue, val);
    };

    const handleClear = () => {
        if (debounceRef.current) clearTimeout(debounceRef.current);
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
