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

function isStringColumn<TData, TValue>(column: Column<TData, TValue>): boolean {
    const paramType = (column.columnDef.meta as { paramType?: string })?.paramType ?? '';
    const baseType = paramType.replace('!', '');
    return baseType === 'String';
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
    const showTextFilter = isStringColumn(column);

    return (
        <DropdownMenu>
            <DropdownMenuTrigger asChild>
                <Button
                    variant="ghost"
                    size="sm"
                    className={cn(
                        '-ml-3 h-8 data-[state=open]:bg-accent',
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
