import { Column, Table } from '@tanstack/react-table';
import { ArrowDown, ArrowUp, ArrowUpDown, EyeOff, Filter, RotateCcw, Key, Link2, Hash, Type, Calendar, ToggleLeft } from 'lucide-react';
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
import { HoverCard, HoverCardTrigger, HoverCardContent } from '@/components/ui/hover-card';
import { TextFilter } from '@/components/filters/text-filter';
import { NumberFilter } from '@/components/filters/number-filter';
import { DateFilter } from '@/components/filters/date-filter';
import { BooleanFilter } from '@/components/filters/boolean-filter';
import { FkFilter } from '@/components/filters/fk-filter';
import type { Column as ColumnSchema } from '@/types/schema';

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

function getColumnSchema<TData, TValue>(column: Column<TData, TValue>): ColumnSchema | undefined {
    return (column.columnDef.meta as { column?: ColumnSchema })?.column;
}

function formatDataType(col: ColumnSchema): string {
    const baseType = col.paramType.replace('!', '');
    
    // Show dbType with length/precision if available
    if (col.dbType) {
        const dbTypeUpper = col.dbType.toUpperCase();
        
        // For string types with maxLength
        if (col.maxLength && (dbTypeUpper.includes('VARCHAR') || dbTypeUpper.includes('CHAR'))) {
            return `${dbTypeUpper}(${col.maxLength})`;
        }
        
        // For DECIMAL/NUMERIC with precision/scale (if we had that data)
        if (dbTypeUpper.includes('DECIMAL') || dbTypeUpper.includes('NUMERIC')) {
            return dbTypeUpper;
        }
        
        return dbTypeUpper;
    }
    
    return baseType;
}

function getTypeIcon(col: ColumnSchema) {
    const baseType = col.paramType.replace('!', '');
    
    if (col.isPrimaryKey) return <Key className="size-3.5" />;
    if (baseType === 'Int' || baseType === 'Float') return <Hash className="size-3.5" />;
    if (baseType === 'DateTime') return <Calendar className="size-3.5" />;
    if (baseType === 'Boolean') return <ToggleLeft className="size-3.5" />;
    return <Type className="size-3.5" />;
}

interface ColumnMetadataTooltipProps {
    column: ColumnSchema;
    fkMeta: { joinTable: string; joinLabelColumn: string } | null;
}

function ColumnMetadataTooltip({ column, fkMeta }: ColumnMetadataTooltipProps) {
    const dataType = formatDataType(column);
    const isNullable = column.isNullable;
    const isPrimaryKey = column.isPrimaryKey;
    const isIdentity = column.isIdentity;
    const defaultValue = column.defaultValue;
    
    return (
        <div className="space-y-2">
            <div className="flex items-center gap-2 border-b pb-2">
                {getTypeIcon(column)}
                <span className="font-semibold text-foreground">{column.label}</span>
            </div>
            
            <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-xs">
                <dt className="text-muted-foreground">Type</dt>
                <dd className="text-foreground font-medium">{dataType}</dd>
                
                <dt className="text-muted-foreground">Nullable</dt>
                <dd className="text-foreground">{isNullable ? 'Yes' : 'No'}</dd>
                
                {isPrimaryKey && (
                    <>
                        <dt className="text-muted-foreground">Primary Key</dt>
                        <dd className="text-foreground text-primary flex items-center gap-1">
                            <Key className="size-3" />
                            Yes
                        </dd>
                    </>
                )}
                
                {isIdentity && (
                    <>
                        <dt className="text-muted-foreground">Auto Increment</dt>
                        <dd className="text-foreground">Yes</dd>
                    </>
                )}
                
                {fkMeta && (
                    <>
                        <dt className="text-muted-foreground">Foreign Key</dt>
                        <dd className="text-foreground text-primary flex items-center gap-1">
                            <Link2 className="size-3" />
                            {fkMeta.joinTable}
                        </dd>
                    </>
                )}
                
                {defaultValue !== undefined && defaultValue !== null && (
                    <>
                        <dt className="text-muted-foreground">Default</dt>
                        <dd className="text-foreground font-mono text-xs truncate max-w-[150px]">{String(defaultValue)}</dd>
                    </>
                )}
                
                {column.maxLength !== undefined && (
                    <>
                        <dt className="text-muted-foreground">Max Length</dt>
                        <dd className="text-foreground">{column.maxLength}</dd>
                    </>
                )}
                
                {column.min !== undefined && (
                    <>
                        <dt className="text-muted-foreground">Min</dt>
                        <dd className="text-foreground">{column.min}</dd>
                    </>
                )}
                
                {column.max !== undefined && (
                    <>
                        <dt className="text-muted-foreground">Max</dt>
                        <dd className="text-foreground">{column.max}</dd>
                    </>
                )}
            </dl>
        </div>
    );
}

interface DataTableColumnHeaderProps<TData, TValue> {
    column: Column<TData, TValue>;
    table: Table<TData>;
    title: string;
}

export function DataTableColumnHeader<TData, TValue>({
    column,
    table: tableInstance,
    title,
}: DataTableColumnHeaderProps<TData, TValue>) {
    const onResetColumnWidths = (tableInstance.options.meta as { onResetColumnWidths?: () => void } | undefined)?.onResetColumnWidths;
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
    const columnSchema = getColumnSchema(column);

    const headerButton = (
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
    );

    return (
        <HoverCard openDelay={400} closeDelay={200}>
            <DropdownMenu>
                <DropdownMenuTrigger asChild>
                    <HoverCardTrigger asChild>
                        {headerButton}
                    </HoverCardTrigger>
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
                    {onResetColumnWidths && (
                        <>
                            <DropdownMenuSeparator />
                            <DropdownMenuItem onClick={onResetColumnWidths}>
                                <RotateCcw className="size-3.5 text-muted-foreground" />
                                Reset column widths
                            </DropdownMenuItem>
                        </>
                    )}
                </DropdownMenuContent>
            </DropdownMenu>
            {columnSchema && (
                <HoverCardContent align="start" className="w-64">
                    <ColumnMetadataTooltip column={columnSchema} fkMeta={fkMeta} />
                </HoverCardContent>
            )}
        </HoverCard>
    );
}
