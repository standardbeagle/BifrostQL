import { Link, usePath } from './hooks/usePath';
import { useSchema } from './hooks/useSchema';
import { Table as SchemaTable } from './types/schema';
import { Loader2, Search, X, ChevronRight, ChevronDown, ChevronLeft } from 'lucide-react';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { Input } from '@/components/ui/input';
import { useTableStats, abbreviateNumber, TableStats } from './hooks/useTableStats';
import { useEditorConfig } from './hooks/useEditorConfig';
import { useMemo, useState, useEffect } from 'react';

/** Tables shown per page. Caps the DOM size so the list stays fast with hundreds of tables. */
const PAGE_SIZE = 50;

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

function safeDecodePathSegment(segment: string): string {
  try {
    return decodeURIComponent(segment);
  } catch {
    return segment;
  }
}

/**
 * Renders a compact sparkline visualization for table statistics.
 *
 * @component
 */
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
  /** Whether this table is the one currently open. */
  active: boolean;
}

/**
 * Renders a table name link, optionally with the stats sparkline.
 *
 * The name uses rem-based type (scales with the host root font) and truncates
 * with ellipsis; `min-w-0` lets the truncation actually engage inside the flex
 * column instead of forcing the row to overflow.
 *
 * @component
 */
function TableNameCell({ table, stats, maxRowCount, showStats, active }: TableNameCellProps) {
  return (
    <Link
      to={`/${table.name}`}
      aria-current={active ? 'page' : undefined}
      className={`plain-link block no-underline py-1.5 px-2 text-sm font-medium border-l-2 ${
        active
          ? 'bg-accent text-accent-foreground border-primary'
          : 'text-foreground border-transparent hover:bg-muted/50 hover:text-primary'
      }`}
    >
      <div className="flex flex-col gap-0.5 min-w-0">
        <span className="truncate" title={table.label}>{table.label}</span>
        {showStats && <TableStatsSparkline stats={stats} maxRowCount={maxRowCount} />}
      </div>
    </Link>
  );
}

/** Schema name a table belongs to, parsed from its `schema.table` dbName; null when unqualified. */
function tableSchema(table: SchemaTable): string | null {
  const dot = table.dbName.lastIndexOf('.');
  return dot > 0 ? table.dbName.slice(0, dot) : null;
}

/** A rendered row: either a collapsible schema header or a table link. */
type ListRow =
  | { kind: 'group'; schema: string; count: number; collapsed: boolean }
  | { kind: 'table'; schema: string | null; table: SchemaTable };

interface SchemaGroupHeaderProps {
  schema: string;
  count: number;
  collapsed: boolean;
  /** Context headers label the current page's leading group but can't be toggled. */
  context?: boolean;
  onToggle?: () => void;
}

/** Collapsible (or static, when `context`) schema section header. */
function SchemaGroupHeader({ schema, count, collapsed, context, onToggle }: SchemaGroupHeaderProps) {
  const Chevron = collapsed ? ChevronRight : ChevronDown;
  const inner = (
    <>
      <Chevron className="size-3.5 shrink-0 text-muted-foreground" />
      <span className="truncate font-semibold" title={schema}>{schema}</span>
      <span className="ml-auto shrink-0 tabular-nums text-[0.85em] text-muted-foreground">{count}</span>
    </>
  );
  const base =
    'flex items-center gap-1.5 w-full px-2 py-1 text-xs uppercase tracking-wide text-muted-foreground bg-muted/40 border-y border-border sticky top-0 z-10';
  if (context) {
    return <div className={base} aria-hidden="true">{inner}</div>;
  }
  return (
    <button
      type="button"
      onClick={onToggle}
      aria-expanded={!collapsed}
      className={`${base} hover:bg-muted/70 text-left`}
    >
      {inner}
    </button>
  );
}

/**
 * TableList — searchable, paged, schema-grouped sidebar of database tables.
 *
 * Scales to several hundred tables:
 * - Search filters by label, raw db name, and schema.
 * - Tables group under collapsible schema headers (only when >1 schema exists).
 * - The visible rows (headers + tables) are paged at {@link PAGE_SIZE} so the DOM
 *   stays small regardless of total table count.
 *
 * @component
 */
export function TableList() {
  const { showStats } = useEditorConfig();
  const { loading: schemaLoading, error: schemaError, data } = useSchema();
  // Only fetch per-table row counts when stats are enabled.
  const { stats, isLoading: statsLoading } = useTableStats(showStats);

  const [query, setQuery] = useState('');
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  const [page, setPage] = useState(0);

  const path = usePath();
  const activeName = safeDecodePathSegment(path.split('/')[1] ?? '');

  const maxRowCount = useMemo(() => {
    const counts = Object.values(stats)
      .map((s) => s.rowCount)
      .filter((c): c is number => c !== null);
    return counts.length > 0 ? Math.max(...counts) : 0;
  }, [stats]);

  // Filter by search across label, db name, and schema.
  const searching = query.trim().length > 0;
  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return data;
    return data.filter((t) => {
      const schema = tableSchema(t);
      return (
        t.label.toLowerCase().includes(q) ||
        t.dbName.toLowerCase().includes(q) ||
        (schema?.toLowerCase().includes(q) ?? false)
      );
    });
  }, [data, query]);

  // Group filtered tables by schema (preserving server order within a group).
  const { schemas, grouped, multiSchema } = useMemo(() => {
    const grouped = new Map<string | null, SchemaTable[]>();
    for (const t of filtered) {
      const schema = tableSchema(t);
      const list = grouped.get(schema) ?? [];
      list.push(t);
      grouped.set(schema, list);
    }
    const named = [...grouped.keys()].filter((s): s is string => s !== null).sort((a, b) => a.localeCompare(b));
    // Distinct *named* schemas decide grouping; an unqualified-only db stays flat.
    return { schemas: named, grouped, multiSchema: named.length > 1 };
  }, [filtered]);

  // Flatten into the rendered row sequence (headers + visible tables). Collapsed
  // groups contribute only their header — unless a search is active, which forces
  // every matching group open so results are never hidden.
  const rows = useMemo<ListRow[]>(() => {
    const out: ListRow[] = [];
    if (multiSchema) {
      const order = [...schemas, ...(grouped.has(null) ? [null as unknown as string] : [])];
      for (const schema of order) {
        const tables = grouped.get(schema === undefined ? null : schema) ?? [];
        const isCollapsed = !searching && collapsed.has(schema);
        out.push({ kind: 'group', schema: schema ?? '(no schema)', count: tables.length, collapsed: isCollapsed });
        if (!isCollapsed) {
          for (const t of tables) out.push({ kind: 'table', schema, table: t });
        }
      }
    } else {
      for (const t of filtered) out.push({ kind: 'table', schema: tableSchema(t), table: t });
    }
    return out;
  }, [multiSchema, schemas, grouped, filtered, collapsed, searching]);

  // Reset to the first page whenever the result set changes shape.
  useEffect(() => { setPage(0); }, [query]);

  const totalPages = Math.max(1, Math.ceil(rows.length / PAGE_SIZE));
  const clampedPage = Math.min(page, totalPages - 1);
  const start = clampedPage * PAGE_SIZE;
  const pageRows = rows.slice(start, start + PAGE_SIZE);

  // When a page begins mid-group, label it with a non-toggle context header so
  // the user always knows which schema the leading tables belong to.
  const leadContext =
    multiSchema && pageRows[0]?.kind === 'table' && pageRows[0].schema
      ? pageRows[0].schema
      : null;

  const toggle = (schema: string) =>
    setCollapsed((prev) => {
      const next = new Set(prev);
      if (next.has(schema)) next.delete(schema); else next.add(schema);
      return next;
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
    // @container so cells adapt to the sidebar's own width; min-w-0 lets descendant
    // truncation engage instead of overflowing. Column flex layout pins the search
    // box on top and the pager on the bottom with the list scrolling between them.
    <div className="@container min-w-0 flex flex-col h-full">
      {/* Search */}
      <div className="sticky top-0 z-20 bg-background p-2 border-b border-border">
        <div className="relative">
          <Search className="absolute left-2 top-1/2 -translate-y-1/2 size-4 text-muted-foreground pointer-events-none" />
          <Input
            type="search"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="Search tables..."
            aria-label="Search tables"
            className="pl-8 pr-8 h-8 text-sm"
          />
          {searching && (
            <button
              type="button"
              onClick={() => setQuery('')}
              aria-label="Clear search"
              className="absolute right-1.5 top-1/2 -translate-y-1/2 p-0.5 rounded text-muted-foreground hover:text-foreground hover:bg-muted"
            >
              <X className="size-4" />
            </button>
          )}
        </div>
        {searching && (
          <p className="mt-1 px-0.5 text-xs text-muted-foreground">
            {filtered.length} match{filtered.length !== 1 ? 'es' : ''}
          </p>
        )}
      </div>

      {/* List */}
      <div className="flex-1 overflow-y-auto min-h-0">
        {rows.length === 0 ? (
          <p className="p-4 text-center text-sm text-muted-foreground">No tables match “{query}”.</p>
        ) : (
          <>
            {leadContext && (
              <SchemaGroupHeader schema={leadContext} count={grouped.get(leadContext)?.length ?? 0} collapsed={false} context />
            )}
            {pageRows.map((row) =>
              row.kind === 'group' ? (
                <SchemaGroupHeader
                  key={`g:${row.schema}`}
                  schema={row.schema}
                  count={row.count}
                  collapsed={row.collapsed}
                  onToggle={() => toggle(row.schema)}
                />
              ) : (
                <TableNameCell
                  key={`t:${row.table.name}`}
                  table={row.table}
                  stats={
                    stats[row.table.name] ?? {
                      columnCount: row.table.columns.length,
                      rowCount: null,
                      fkCount: row.table.singleJoins.length,
                      isLoading: statsLoading,
                      error: null,
                    }
                  }
                  maxRowCount={maxRowCount}
                  showStats={showStats}
                  active={row.table.name === activeName}
                />
              )
            )}
          </>
        )}
      </div>

      {/* Pager */}
      {totalPages > 1 && (
        <div className="sticky bottom-0 z-20 bg-background border-t border-border flex items-center justify-between gap-2 px-2 py-1.5">
          <button
            type="button"
            onClick={() => setPage((p) => Math.max(0, p - 1))}
            disabled={clampedPage === 0}
            aria-label="Previous page"
            className="p-1 rounded text-muted-foreground hover:text-foreground hover:bg-muted disabled:opacity-40 disabled:pointer-events-none"
          >
            <ChevronLeft className="size-4" />
          </button>
          <span className="text-xs text-muted-foreground tabular-nums">
            {start + 1}–{Math.min(start + PAGE_SIZE, rows.length)} of {rows.length}
          </span>
          <button
            type="button"
            onClick={() => setPage((p) => Math.min(totalPages - 1, p + 1))}
            disabled={clampedPage >= totalPages - 1}
            aria-label="Next page"
            className="p-1 rounded text-muted-foreground hover:text-foreground hover:bg-muted disabled:opacity-40 disabled:pointer-events-none"
          >
            <ChevronRight className="size-4" />
          </button>
        </div>
      )}
    </div>
  );
}
