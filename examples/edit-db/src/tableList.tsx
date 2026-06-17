import { ColumnDef, flexRender, getCoreRowModel, useReactTable } from '@tanstack/react-table';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Link } from './hooks/usePath';
import { useSchema } from './hooks/useSchema';
import { Table as SchemaTable } from './types/schema';
import { Loader2 } from 'lucide-react';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { useTableStats, abbreviateNumber, TableStats } from './hooks/useTableStats';
import { useEditorConfig } from './hooks/useEditorConfig';
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
/** Row-count fill as a percentage (min 2% so any non-zero count stays visible). */
function rowCountPercent(count: number | null, maxCount: number): number {
  if (count === null || maxCount <= 0) return 0;
  return Math.max(2, Math.min(100, Math.round((count / maxCount) * 100)));
}

/**
 * A real, element-based bar — height in `em` (scales with the surrounding font
 * size) and width that flexes to fill the container — so the stat scales up with
 * both font size and panel width instead of being pinned to a fixed block string.
 */
function StatBar({ percent, label }: { percent: number; label: string }) {
  return (
    <span
      className="flex-1 min-w-[1.5em] h-[0.5em] rounded-full bg-muted/70 overflow-hidden"
      role="img"
      aria-label={label}
      title={label}
    >
      <span
        className="block h-full rounded-full bg-emerald-500/70 dark:bg-emerald-400/70"
        style={{ width: `${percent}%` }}
      />
    </span>
  );
}

function FkBadge({ count }: { count: number }) {
  if (count <= 0) return null;
  return (
    <span className="shrink-0 rounded px-1 py-px text-[0.85em] tabular-nums bg-blue-100 text-blue-700 dark:bg-blue-900/60 dark:text-blue-300">
      {count} FK
    </span>
  );
}

function TableStatsSparkline({ stats, maxRowCount }: TableStatsSparklineProps) {
  const percent = rowCountPercent(stats.rowCount, maxRowCount);
  // All sizing is relative: `text-xs` is rem-based (scales with the host root font),
  // the bar height is `em` (scales with this row's font), and widths flex to the
  // container — so the stats grow with both font size and panel width.
  const base = "flex items-center gap-1.5 text-xs leading-none min-w-0";

  if (stats.isLoading) {
    return (
      <div className={`${base} text-muted-foreground`}>
        <span className="tabular-nums shrink-0">{stats.columnCount}c</span>
        <span className="flex-1 min-w-[1.5em] h-[0.5em] rounded-full bg-muted/70 overflow-hidden">
          <span className="block h-full w-1/3 rounded-full bg-muted-foreground/30 animate-pulse" />
        </span>
        <Loader2 className="size-[1em] shrink-0 animate-spin" />
        <FkBadge count={stats.fkCount} />
      </div>
    );
  }

  if (stats.error) {
    return (
      <div className={base}>
        <span className="tabular-nums shrink-0 text-muted-foreground">{stats.columnCount}c</span>
        <span className="flex-1 text-destructive truncate" title={stats.error}>err</span>
        <FkBadge count={stats.fkCount} />
      </div>
    );
  }

  return (
    <div className={base}>
      <span className="tabular-nums shrink-0 text-muted-foreground/70">{stats.columnCount}c</span>
      <StatBar percent={percent} label={`${stats.rowCount?.toLocaleString() ?? 'unknown'} rows`} />
      <span className="tabular-nums shrink-0 text-right text-muted-foreground">{abbreviateNumber(stats.rowCount)}</span>
      <FkBadge count={stats.fkCount} />
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
  /** Whether to render the stats sparkline row. */
  showStats: boolean;
}

/**
 * Renders a table name cell, optionally with the stats sparkline.
 *
 * The name uses rem-based type (scales with the host root font) and truncates
 * with ellipsis; `min-w-0` lets the truncation actually engage inside the flex
 * column instead of forcing the row to overflow.
 *
 * @component
 */
function TableNameCell({ table, stats, maxRowCount, showStats }: TableNameCellProps) {
  return (
    <Link
      to={`/${table.name}`}
      className="plain-link block no-underline text-foreground hover:text-primary py-1.5 px-2 text-sm font-medium group"
    >
      <div className="flex flex-col gap-0.5 min-w-0">
        <span className="truncate" title={table.label}>{table.label}</span>
        {showStats && <TableStatsSparkline stats={stats} maxRowCount={maxRowCount} />}
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
  const { showStats } = useEditorConfig();
  const { loading: schemaLoading, error: schemaError, data } = useSchema();
  // Only fetch per-table row counts when stats are enabled.
  const { stats, isLoading: statsLoading } = useTableStats(showStats);

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
            showStats={showStats}
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
    // Container context so cells can adapt to the sidebar's own width (not the
    // viewport); min-w-0 lets descendant truncation engage instead of overflowing.
    <div className="@container min-w-0">
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
