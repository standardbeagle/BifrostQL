import { ColumnDef, flexRender, getCoreRowModel, useReactTable } from '@tanstack/react-table';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Link } from './hooks/usePath';
import { useSchema } from './hooks/useSchema';
import { Table as SchemaTable } from './types/schema';
import { Loader2 } from 'lucide-react';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { useTableStats, abbreviateNumber, calculateBarWidth, TableStats } from './hooks/useTableStats';
import { useMemo } from 'react';

/**
 * Props for the TableStatsSparkline component.
 * @interface TableStatsSparklineProps
 */
interface TableStatsSparklineProps {
  /** Table statistics to display */
  stats: TableStats;
  /** Maximum row count for bar scaling */
  maxRowCount: number;
}

/**
 * Renders a compact sparkline visualization for table statistics.
 * 
 * Displays:
 * - Column count as a small muted badge
 * - Row count as an abbreviated number with a visual bar
 * - FK count as a colored badge (only if > 0)
 * 
 * @component
 * @example
 * ```tsx
 * <TableStatsSparkline stats={stats} maxRowCount={10000} />
 * ```
 */
function TableStatsSparkline({ stats, maxRowCount }: TableStatsSparklineProps) {
  const barWidth = calculateBarWidth(stats.rowCount, maxRowCount);
  const barBlocks = '█'.repeat(barWidth);
  const emptyBlocks = '░'.repeat(10 - barWidth);

  if (stats.isLoading) {
    return (
      <div className="flex items-center gap-1.5 text-muted-foreground">
        <span className="text-[10px] tabular-nums">{stats.columnCount} cols</span>
        <Loader2 className="size-3 animate-spin" />
        <span className="text-[10px] text-muted-foreground/60">loading…</span>
        {stats.fkCount > 0 && (
          <span className="text-[10px] px-1 py-0.5 rounded bg-blue-100 text-blue-700 dark:bg-blue-900 dark:text-blue-300">
            {stats.fkCount} FK
          </span>
        )}
      </div>
    );
  }

  if (stats.error) {
    return (
      <div className="flex items-center gap-1.5">
        <span className="text-[10px] tabular-nums text-muted-foreground">{stats.columnCount} cols</span>
        <span className="text-[10px] text-destructive" title={stats.error}>err</span>
        {stats.fkCount > 0 && (
          <span className="text-[10px] px-1 py-0.5 rounded bg-blue-100 text-blue-700 dark:bg-blue-900 dark:text-blue-300">
            {stats.fkCount} FK
          </span>
        )}
      </div>
    );
  }

  return (
    <div className="flex items-center gap-1.5">
      {/* Column count - muted */}
      <span className="text-[10px] tabular-nums text-muted-foreground/70 w-8 text-right shrink-0">
        {stats.columnCount}c
      </span>
      
      {/* Row count bar */}
      <span 
        className="text-[10px] font-mono text-emerald-600 dark:text-emerald-400 shrink-0"
        title={`${stats.rowCount?.toLocaleString() ?? 'unknown'} rows`}
      >
        {barBlocks}
        {emptyBlocks}
      </span>
      
      {/* Row count number */}
      <span className="text-[10px] tabular-nums text-muted-foreground w-10 shrink-0">
        {abbreviateNumber(stats.rowCount)}
      </span>
      
      {/* FK badge - only if > 0 */}
      {stats.fkCount > 0 && (
        <span className="text-[10px] px-1 py-0.5 rounded bg-blue-100 text-blue-700 dark:bg-blue-900 dark:text-blue-300 shrink-0">
          {stats.fkCount} FK
        </span>
      )}
    </div>
  );
}

/**
 * Props for the TableNameCell component.
 * @interface TableNameCellProps
 */
interface TableNameCellProps {
  /** Table data from schema */
  table: SchemaTable;
  /** Statistics for this table */
  stats: TableStats;
  /** Maximum row count for bar scaling */
  maxRowCount: number;
}

/**
 * Renders a table name cell with sparkline statistics.
 * 
 * @component
 */
function TableNameCell({ table, stats, maxRowCount }: TableNameCellProps) {
  return (
    <Link 
      to={`/${table.name}`} 
      className="block no-underline text-foreground hover:text-primary py-1.5 px-2 text-xs font-medium group"
    >
      <div className="flex flex-col gap-0.5">
        <span className="truncate">{table.label}</span>
        <TableStatsSparkline stats={stats} maxRowCount={maxRowCount} />
      </div>
    </Link>
  );
}

/**
 * TableList component that displays database tables in a sidebar.
 * 
 * Features:
 * - Lists all tables from the schema
 * - Shows loading and error states
 * - Displays table statistics (columns, rows, FKs) as sparklines
 * - Responsive layout with visual hierarchy
 * 
 * @component
 * @example
 * ```tsx
 * <TableList />
 * ```
 */
export function TableList() {
  const { loading: schemaLoading, error: schemaError, data } = useSchema();
  const { stats, isLoading: statsLoading } = useTableStats();

  const maxRowCount = useMemo(() => {
    const counts = Object.values(stats)
      .map((s) => s.rowCount)
      .filter((c): c is number => c !== null);
    return counts.length > 0 ? Math.max(...counts) : 0;
  }, [stats]);

  const columns: ColumnDef<SchemaTable, unknown>[] = [
    {
      id: 'table',
      header: 'Table',
      cell: ({ row }) => {
        const table = row.original;
        const tableStats = stats[table.name] ?? {
          columnCount: table.columns.length,
          rowCount: null,
          fkCount: table.singleJoins.length,
          isLoading: statsLoading,
          error: null,
        };
        return (
          <TableNameCell 
            table={table} 
            stats={tableStats} 
            maxRowCount={maxRowCount} 
          />
        );
      },
    }
  ];

  const table = useReactTable({
    data,
    columns,
    getCoreRowModel: getCoreRowModel(),
  });

  if (schemaLoading) return (
    <div className="flex items-center justify-center gap-2 p-4">
      <Loader2 className="size-4 animate-spin text-muted-foreground" />
      <span className="text-sm text-muted-foreground">Loading...</span>
    </div>
  );
  
  if (schemaError) return (
    <Alert variant="destructive" className="m-2">
      <AlertDescription>Error: {schemaError.message}</AlertDescription>
    </Alert>
  );
  
  if (data.length === 0) return (
    <div className="p-4 text-center text-sm text-muted-foreground">
      <p className="font-medium">No tables found</p>
      <p className="mt-1">The database may be empty or the connection may have failed.</p>
    </div>
  );

  return (
    <div>
      <Table>
        <TableHeader>
          {table.getHeaderGroups().map((headerGroup) => (
            <TableRow key={headerGroup.id}>
              {headerGroup.headers.map((header) => (
                <TableHead key={header.id}>
                  {header.isPlaceholder
                    ? null
                    : flexRender(header.column.columnDef.header, header.getContext())}
                </TableHead>
              ))}
            </TableRow>
          ))}
        </TableHeader>
        <TableBody>
          {table.getRowModel().rows.map((row) => (
            <TableRow key={row.id}>
              {row.getVisibleCells().map((cell) => (
                <TableCell key={cell.id} className="p-0">
                  {flexRender(cell.column.columnDef.cell, cell.getContext())}
                </TableCell>
              ))}
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}
