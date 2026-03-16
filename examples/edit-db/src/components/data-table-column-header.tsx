import { Column } from '@tanstack/react-table';
import { ArrowDown, ArrowUp, ArrowUpDown, EyeOff, Filter } from 'lucide-react';
import { cn } from '@/lib/utils';
import { Button } from '@/components/ui/button';
import {
    DropdownMenu,
    DropdownMenuContent,
    DropdownMenuItem,
    DropdownMenuSeparator,
    DropdownMenuSub,
    DropdownMenuSubContent,
    DropdownMenuSubTrigger,
    DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu';
import { TextFilter } from '@/components/filters/text-filter';
import { NumberFilter } from '@/components/filters/number-filter';
import { DateFilter } from '@/components/filters/date-filter';
import { BooleanFilter } from '@/components/filters/boolean-filter';
import { FkFilter } from '@/components/filters/fk-filter';

function getBaseParamType<TData, TValue>(column: Column<TData, TValue>): string {
    const paramType = (column.columnDef.meta as { paramType?: string })?.paramType ?? '';
    return paramType.replace('!', '');
}

function isStringColumn<TData, TValue>(column: Column<TData, TValue>): boolean {
    return getBaseParamType(column) === 'String';
}

function isNumericColumn<TData, TValue>(column: Column<TData, TValue>): boolean {
    const base = getBaseParamType(column);
    return base === 'Int' || base === 'Float';
}

function isDateColumn<TData, TValue>(column: Column<TData, TValue>): boolean {
    return getBaseParamType(column) === 'DateTime';
}

function isBooleanColumn<TData, TValue>(column: Column<TData, TValue>): boolean {
    return getBaseParamType(column) === 'Boolean';
}

function getFkMeta<TData, TValue>(column: Column<TData, TValue>): { joinTable: string; joinLabelColumn: string } | null {
    const meta = column.columnDef.meta as { joinTable?: string; joinLabelColumn?: string } | undefined;
    if (meta?.joinTable) return { joinTable: meta.joinTable, joinLabelColumn: meta.joinLabelColumn ?? 'id' };
    return null;
}

interface DataTableColumnHeaderProps<TData, TValue> {
    column: Column<TData, TValue>;
    title: string;
}

export function DataTableColumnHeader<TData, TValue>({
    column,
    title,
}: DataTableColumnHeaderProps<TData, TValue>) {
    if (!column.getCanSort()) {
        return <span>{title}</span>;
    }

    const sorted = column.getIsSorted();
    const hasFilter = column.getFilterValue() !== undefined;
    const fkMeta = getFkMeta(column);
    const showTextFilter = !fkMeta && isStringColumn(column);
    const showNumericFilter = !fkMeta && isNumericColumn(column);
    const showDateFilter = !fkMeta && isDateColumn(column);
    const showBooleanFilter = !fkMeta && isBooleanColumn(column);

    return (
        <DropdownMenu>
            <DropdownMenuTrigger asChild>
                <Button
                    variant="ghost"
                    size="sm"
                    className={cn(
                        '-ml-2 h-7 text-xs data-[state=open]:bg-accent',
                        sorted && 'text-foreground'
                    )}
                >
                    <span>{title}</span>
                    {hasFilter && (
                        <Filter className="size-3 text-primary" />
                    )}
                    {sorted === 'desc' ? (
                        <ArrowDown className="size-4" />
                    ) : sorted === 'asc' ? (
                        <ArrowUp className="size-4" />
                    ) : (
                        <ArrowUpDown className="size-4" />
                    )}
                </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="start">
                <DropdownMenuItem onClick={() => column.toggleSorting(false)}>
                    <ArrowUp className="size-3.5 text-muted-foreground" />
                    Asc
                </DropdownMenuItem>
                <DropdownMenuItem onClick={() => column.toggleSorting(true)}>
                    <ArrowDown className="size-3.5 text-muted-foreground" />
                    Desc
                </DropdownMenuItem>
                {sorted && (
                    <DropdownMenuItem onClick={() => column.clearSorting()}>
                        <ArrowUpDown className="size-3.5 text-muted-foreground" />
                        Clear
                    </DropdownMenuItem>
                )}
                {showTextFilter && (
                    <>
                        <DropdownMenuSeparator />
                        <DropdownMenuSub>
                            <DropdownMenuSubTrigger>
                                <Filter className={cn(
                                    'size-3.5',
                                    hasFilter ? 'text-primary' : 'text-muted-foreground'
                                )} />
                                Filter
                            </DropdownMenuSubTrigger>
                            <DropdownMenuSubContent>
                                <TextFilter column={column} />
                            </DropdownMenuSubContent>
                        </DropdownMenuSub>
                    </>
                )}
                {showNumericFilter && (
                    <>
                        <DropdownMenuSeparator />
                        <DropdownMenuSub>
                            <DropdownMenuSubTrigger>
                                <Filter className={cn(
                                    'size-3.5',
                                    hasFilter ? 'text-primary' : 'text-muted-foreground'
                                )} />
                                Filter
                            </DropdownMenuSubTrigger>
                            <DropdownMenuSubContent>
                                <NumberFilter column={column} />
                            </DropdownMenuSubContent>
                        </DropdownMenuSub>
                    </>
                )}
                {showDateFilter && (
                    <>
                        <DropdownMenuSeparator />
                        <DropdownMenuSub>
                            <DropdownMenuSubTrigger>
                                <Filter className={cn(
                                    'size-3.5',
                                    hasFilter ? 'text-primary' : 'text-muted-foreground'
                                )} />
                                Filter
                            </DropdownMenuSubTrigger>
                            <DropdownMenuSubContent>
                                <DateFilter column={column} />
                            </DropdownMenuSubContent>
                        </DropdownMenuSub>
                    </>
                )}
                {showBooleanFilter && (
                    <>
                        <DropdownMenuSeparator />
                        <DropdownMenuSub>
                            <DropdownMenuSubTrigger>
                                <Filter className={cn(
                                    'size-3.5',
                                    hasFilter ? 'text-primary' : 'text-muted-foreground'
                                )} />
                                Filter
                            </DropdownMenuSubTrigger>
                            <DropdownMenuSubContent>
                                <BooleanFilter column={column} />
                            </DropdownMenuSubContent>
                        </DropdownMenuSub>
                    </>
                )}
                {fkMeta && (
                    <>
                        <DropdownMenuSeparator />
                        <DropdownMenuSub>
                            <DropdownMenuSubTrigger>
                                <Filter className={cn(
                                    'size-3.5',
                                    hasFilter ? 'text-primary' : 'text-muted-foreground'
                                )} />
                                Filter
                            </DropdownMenuSubTrigger>
                            <DropdownMenuSubContent>
                                <FkFilter column={column} joinTable={fkMeta.joinTable} joinLabelColumn={fkMeta.joinLabelColumn} />
                            </DropdownMenuSubContent>
                        </DropdownMenuSub>
                    </>
                )}
                {column.getCanHide() && (
                    <>
                        <DropdownMenuSeparator />
                        <DropdownMenuItem onClick={() => column.toggleVisibility(false)}>
                            <EyeOff className="size-3.5 text-muted-foreground" />
                            Hide
                        </DropdownMenuItem>
                    </>
                )}
            </DropdownMenuContent>
        </DropdownMenu>
    );
}
