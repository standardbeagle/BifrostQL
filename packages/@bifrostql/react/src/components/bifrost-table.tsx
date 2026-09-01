import { useState, useCallback, createContext, useContext } from 'react';
import type { CSSProperties, ReactNode } from 'react';
import { useBifrostTable } from '../hooks/use-bifrost-table';
import type {
  ColumnConfig,
  PaginationConfig,
  ChildQueryConfig,
  ExpansionState,
  EditingState,
  RowUpdateFn,
  SaveErrorFn,
  UseBifrostTableOptions,
} from '../hooks/use-bifrost-table';
import type { UseBifrostOptions } from '../hooks/use-bifrost';
import type {
  FieldNameOf,
  SortOption,
  SortOptionFor,
  TableFilter,
} from '../types';
import { rowsToCsv, triggerDownload } from '../utils/table-export';
import { getTheme } from './table-theme';
import type { AnyThemeName, TableTheme } from './table-theme';

// ---------------------------------------------------------------------------
// Theme context for composable sub-components
// ---------------------------------------------------------------------------

interface TableThemeContextValue {
  theme: TableTheme;
}

const TableThemeContext = createContext<TableThemeContextValue>({
  theme: getTheme('modern'),
});

function useTableTheme(): TableTheme {
  return useContext(TableThemeContext).theme;
}

// ---------------------------------------------------------------------------
// Shared helpers
// ---------------------------------------------------------------------------

function formatCellValue(value: unknown): string {
  if (value === null || value === undefined) return '';
  if (typeof value === 'boolean') return value ? 'Yes' : 'No';
  if (value instanceof Date) return value.toLocaleDateString();
  return String(value);
}

/** Download the given rows as CSV, rendering cells the way the table does. */
function downloadRowsAsCsv<T>(
  data: T[],
  columns: ColumnConfig[],
  table: string,
): void {
  const fields = columns.map((c) => c.field);
  const headers = columns.map((c) => c.header);
  const formatters = Object.fromEntries(
    fields.map((field) => [field, (value: unknown) => formatCellValue(value)]),
  );
  const csv = rowsToCsv(
    data as Record<string, unknown>[],
    fields,
    headers,
    formatters,
  );
  triggerDownload(csv, `${table}-export.csv`, 'text/csv;charset=utf-8;');
}

function getSortDirection(
  sort: SortOption[],
  field: string,
): 'asc' | 'desc' | null {
  const entry = sort.find((s) => s.field === field);
  return entry?.direction ?? null;
}

function SortArrow({ direction }: { direction: 'asc' | 'desc' | null }) {
  if (direction === 'asc')
    return <span aria-label="sorted ascending">{'\u2191'}</span>;
  if (direction === 'desc')
    return <span aria-label="sorted descending">{'\u2193'}</span>;
  return <span aria-label="unsorted">{'\u2195'}</span>;
}

// ---------------------------------------------------------------------------
// Composable sub-components
// ---------------------------------------------------------------------------

export interface TableHeaderProps {
  columns: ColumnConfig[];
  sortState?: SortOption[];
  onSort?: (field: string) => void;
  style?: CSSProperties;
  children?: ReactNode;
}

export function TableHeader({
  columns,
  sortState = [],
  onSort,
  style,
  children,
}: TableHeaderProps) {
  const theme = useTableTheme();

  if (children) {
    return <thead style={style}>{children}</thead>;
  }

  return (
    <thead style={style}>
      <tr style={theme.headerRow} role="row">
        {columns.map((col) => {
          const sortDir = getSortDirection(sortState, col.field);
          const isSortable =
            col.sortable &&
            col.field !== '__actions' &&
            col.field !== '__expand';
          return (
            <th
              key={col.field}
              style={{
                ...theme.headerCell,
                ...(col.width ? { width: col.width } : {}),
                ...(isSortable ? { cursor: 'pointer' } : {}),
              }}
              role="columnheader"
              aria-sort={
                sortDir === 'asc'
                  ? 'ascending'
                  : sortDir === 'desc'
                    ? 'descending'
                    : 'none'
              }
              onClick={
                isSortable && onSort ? () => onSort(col.field) : undefined
              }
            >
              {col.header}
              {isSortable && (
                <span style={theme.sortIndicator}>
                  <SortArrow direction={sortDir} />
                </span>
              )}
            </th>
          );
        })}
      </tr>
    </thead>
  );
}

export interface TableBodyProps {
  children: ReactNode;
  style?: CSSProperties;
}

export function TableBody({ children, style }: TableBodyProps) {
  return <tbody style={style}>{children}</tbody>;
}

export interface TableRowProps {
  style?: CSSProperties;
  onClick?: () => void;
  hoverable?: boolean;
  striped?: boolean;
  children: ReactNode;
  testId?: string;
}

export function TableRow({ style, onClick, children, testId }: TableRowProps) {
  const theme = useTableTheme();

  return (
    <tr
      style={{ ...theme.bodyRow, ...style }}
      role="row"
      onClick={onClick}
      data-testid={testId}
    >
      {children}
    </tr>
  );
}

export interface TableCellProps {
  style?: CSSProperties;
  children?: ReactNode;
  colSpan?: number;
}

export function TableCell({ style, children, colSpan }: TableCellProps) {
  const theme = useTableTheme();

  return (
    <td style={{ ...theme.bodyCell, ...style }} role="cell" colSpan={colSpan}>
      {children}
    </td>
  );
}

export interface TableFooterProps {
  children: ReactNode;
  style?: CSSProperties;
}

export function TableFooter({ children, style }: TableFooterProps) {
  return (
    <div data-testid="table-footer" style={style}>
      {children}
    </div>
  );
}

export interface TableToolbarProps {
  children: ReactNode;
  style?: CSSProperties;
}

export function TableToolbar({ children, style }: TableToolbarProps) {
  const theme = useTableTheme();

  return (
    <div style={{ ...theme.toolbar, ...style }} data-testid="table-toolbar">
      {children}
    </div>
  );
}

export interface ExpandedRowProps {
  colSpan: number;
  children: ReactNode;
  style?: CSSProperties;
  testId?: string;
}

export function ExpandedRow({
  colSpan,
  children,
  style,
  testId,
}: ExpandedRowProps) {
  const theme = useTableTheme();

  return (
    <tr
      style={{ ...theme.expandedRow, ...style }}
      role="row"
      data-testid={testId}
    >
      <td colSpan={colSpan} role="cell" style={theme.expandedRowContent}>
        {children}
      </td>
    </tr>
  );
}

export interface PaginationProps {
  page: number;
  pageSize: number;
  dataLength: number;
  onPrevious: () => void;
  onNext: () => void;
  style?: CSSProperties;
}

export function Pagination({
  page,
  pageSize,
  dataLength,
  onPrevious,
  onNext,
  style,
}: PaginationProps) {
  const theme = useTableTheme();

  return (
    <div style={{ ...theme.pagination, ...style }} data-testid="pagination">
      <button
        type="button"
        style={
          page === 0 ? theme.paginationButtonDisabled : theme.paginationButton
        }
        disabled={page === 0}
        onClick={onPrevious}
        data-testid="pagination-prev"
      >
        Previous
      </button>
      <span style={theme.paginationInfo} data-testid="pagination-info">
        Page {page + 1}
      </span>
      <button
        type="button"
        style={
          dataLength < pageSize
            ? theme.paginationButtonDisabled
            : theme.paginationButton
        }
        disabled={dataLength < pageSize}
        onClick={onNext}
        data-testid="pagination-next"
      >
        Next
      </button>
    </div>
  );
}

export interface ColumnSelectorProps {
  columns: ColumnConfig[];
  visibleFields: Set<string>;
  onToggle: (field: string) => void;
  style?: CSSProperties;
}

export function ColumnSelector({
  columns,
  visibleFields,
  onToggle,
  style,
}: ColumnSelectorProps) {
  const theme = useTableTheme();
  const [open, setOpen] = useState(false);

  return (
    <div
      style={{ position: 'relative', ...style }}
      data-testid="column-selector"
    >
      <button
        type="button"
        style={theme.toolbarButton}
        onClick={() => setOpen((v) => !v)}
        data-testid="column-selector-toggle"
      >
        Columns
      </button>
      {open && (
        <div
          style={{
            position: 'absolute',
            right: 0,
            top: '100%',
            zIndex: 10,
            background: theme.actionButton.background as string,
            border: `1px solid ${theme.actionButton.borderColor ?? '#d1d5db'}`,
            borderRadius: theme.actionButton.borderRadius as string,
            padding: '8px',
            minWidth: '160px',
          }}
          data-testid="column-selector-menu"
        >
          {columns.map((col) => (
            <label
              key={col.field}
              style={{
                display: 'flex',
                alignItems: 'center',
                gap: '6px',
                padding: '4px 0',
                fontSize: '13px',
                color: theme.bodyCell.color as string,
                cursor: 'pointer',
              }}
            >
              <input
                type="checkbox"
                checked={visibleFields.has(col.field)}
                onChange={() => onToggle(col.field)}
                data-testid={`column-toggle-${col.field}`}
              />
              {col.header}
            </label>
          ))}
        </div>
      )}
    </div>
  );
}

export interface FilterBuilderProps {
  columns: ColumnConfig[];
  onApply: (filter: TableFilter) => void;
  style?: CSSProperties;
}

export function FilterBuilder({ columns, onApply, style }: FilterBuilderProps) {
  const theme = useTableTheme();
  const filterableColumns = columns.filter((c) => c.filterable);
  const [field, setField] = useState(filterableColumns[0]?.field ?? '');
  const [value, setValue] = useState('');

  const handleApply = () => {
    if (!field || !value) return;
    onApply({ [field]: { _contains: value } });
    setValue('');
  };

  if (filterableColumns.length === 0) return null;

  return (
    <div
      style={{ display: 'flex', gap: '4px', alignItems: 'center', ...style }}
      data-testid="filter-builder"
    >
      <select
        value={field}
        onChange={(e) => setField(e.target.value)}
        style={{
          ...theme.toolbarButton,
          padding: '4px 8px',
        }}
        data-testid="filter-field-select"
      >
        {filterableColumns.map((col) => (
          <option key={col.field} value={col.field}>
            {col.header}
          </option>
        ))}
      </select>
      <input
        type="text"
        value={value}
        onChange={(e) => setValue(e.target.value)}
        placeholder="Filter value..."
        onKeyDown={(e) => {
          if (e.key === 'Enter') handleApply();
        }}
        style={{
          padding: '4px 8px',
          border: `1px solid ${theme.actionButton.borderColor ?? '#d1d5db'}`,
          borderRadius: theme.actionButton.borderRadius as string,
          fontSize: '12px',
          outline: 'none',
        }}
        data-testid="filter-value-input"
      />
      <button
        type="button"
        style={theme.toolbarButton}
        onClick={handleApply}
        data-testid="filter-apply-button"
      >
        Apply
      </button>
    </div>
  );
}

/**
 * Formats the standalone ExportMenu can emit — a strict subset of the
 * table-hook's `ExportFormat` ('excel' is only available via
 * `useBifrostTable`'s export state).
 */
export type ExportMenuFormat = 'csv' | 'json';

export interface ExportMenuProps {
  data: Record<string, unknown>[];
  columns: ColumnConfig[];
  queryName: string;
  formats?: ExportMenuFormat[];
  style?: CSSProperties;
}

export function ExportMenu({
  data,
  columns,
  queryName,
  formats = ['csv', 'json'],
  style,
}: ExportMenuProps) {
  const theme = useTableTheme();
  const [open, setOpen] = useState(false);

  const handleExportCsv = () => {
    downloadRowsAsCsv(data, columns, queryName);
    setOpen(false);
  };

  const handleExportJson = () => {
    const json = JSON.stringify(data, null, 2);
    triggerDownload(json, `${queryName}-export.json`, 'application/json');
    setOpen(false);
  };

  return (
    <div style={{ position: 'relative', ...style }} data-testid="export-menu">
      <button
        type="button"
        style={theme.toolbarButton}
        onClick={() => setOpen((v) => !v)}
        data-testid="export-menu-toggle"
      >
        Export
      </button>
      {open && (
        <div
          style={{
            position: 'absolute',
            right: 0,
            top: '100%',
            zIndex: 10,
            background: theme.actionButton.background as string,
            border: `1px solid ${theme.actionButton.borderColor ?? '#d1d5db'}`,
            borderRadius: theme.actionButton.borderRadius as string,
            padding: '4px',
            minWidth: '120px',
          }}
          data-testid="export-menu-dropdown"
        >
          {formats.includes('csv') && (
            <button
              type="button"
              style={{
                ...theme.toolbarButton,
                display: 'block',
                width: '100%',
                textAlign: 'left',
                border: 'none',
              }}
              onClick={handleExportCsv}
              data-testid="export-csv-button"
            >
              CSV
            </button>
          )}
          {formats.includes('json') && (
            <button
              type="button"
              style={{
                ...theme.toolbarButton,
                display: 'block',
                width: '100%',
                textAlign: 'left',
                border: 'none',
              }}
              onClick={handleExportJson}
              data-testid="export-json-button"
            >
              JSON
            </button>
          )}
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Main BifrostTable component
// ---------------------------------------------------------------------------

export interface RowAction<T = Record<string, unknown>> {
  label: string;
  onClick: (row: T) => void;
}

interface TableRowsProps<T> {
  data: T[];
  visibleColumns: ColumnConfig[];
  rowKey: string;
  striped: boolean;
  hoverable: boolean;
  editable: boolean;
  expandable: boolean;
  childQuery: ChildQueryConfig | undefined;
  expansion: ExpansionState;
  onRowClick: ((row: T) => void) | undefined;
  rowActions: RowAction<T>[] | undefined;
  renderRow:
    | ((row: T, rowIndex: number, defaultRow: ReactNode) => ReactNode)
    | undefined;
  renderCell:
    | ((value: unknown, row: T, column: ColumnConfig) => ReactNode)
    | undefined;
  renderExpandedRow: ((row: T) => ReactNode) | undefined;
  hoveredRowIndex: number | null;
  setHoveredRowIndex: (index: number | null) => void;
  /**
   * The table hook's editing surface. Inline edits are committed through it,
   * so a keystroke actually reaches `onRowUpdate`.
   */
  editing: EditingState;
}

/**
 * Renders the data rows (and their expanded child rows) of a BifrostTable.
 * Extracted from BifrostTable so the main component body stays composition
 * only; consumes the shared theme via context like the other sub-components.
 */
function TableRows<T>({
  data,
  visibleColumns,
  rowKey,
  striped,
  hoverable,
  editable,
  expandable,
  childQuery,
  expansion,
  onRowClick,
  rowActions,
  renderRow,
  renderCell,
  renderExpandedRow,
  hoveredRowIndex,
  setHoveredRowIndex,
  editing,
}: TableRowsProps<T>) {
  const theme = useTableTheme();

  return (
    <>
      {data.map((row, rowIndex) => {
        const rowRecord = row as Record<string, unknown>;
        const key = String(rowRecord[rowKey] ?? rowIndex);
        const isHovered = hoverable && hoveredRowIndex === rowIndex;
        const isStriped = striped && rowIndex % 2 === 1;
        const isExpanded = expandable && expansion.expandedRows.has(key);

        const rowStyle: CSSProperties = {
          ...theme.bodyRow,
          ...(isStriped ? theme.bodyRowStriped : {}),
          ...(isHovered ? theme.bodyRowHover : {}),
          ...(onRowClick ? { cursor: 'pointer' } : {}),
        };

        const handleExpandToggle = () => {
          expansion.toggleExpand(key);
          if (!expansion.expandedRows.has(key) && childQuery) {
            expansion.fetchChildData(key, rowRecord);
          }
        };

        const handleRowKeyDown = expandable
          ? (e: React.KeyboardEvent) => {
              if (e.key === 'ArrowRight' && !isExpanded) {
                e.preventDefault();
                handleExpandToggle();
              } else if (e.key === 'ArrowLeft' && isExpanded) {
                e.preventDefault();
                handleExpandToggle();
              }
            }
          : undefined;

        const rows: ReactNode[] = [];

        const defaultRowElement = (
          <tr
            key={key}
            style={rowStyle}
            role="row"
            tabIndex={expandable ? 0 : undefined}
            onClick={onRowClick ? () => onRowClick(row) : undefined}
            onKeyDown={handleRowKeyDown}
            onMouseEnter={
              hoverable ? () => setHoveredRowIndex(rowIndex) : undefined
            }
            onMouseLeave={
              hoverable ? () => setHoveredRowIndex(null) : undefined
            }
            data-testid={`table-row-${key}`}
          >
            {visibleColumns.map((col) => {
              if (col.field === '__expand') {
                return (
                  <td
                    key="__expand"
                    style={theme.bodyCell}
                    role="cell"
                    onClick={(e) => {
                      e.stopPropagation();
                      handleExpandToggle();
                    }}
                  >
                    <button
                      type="button"
                      style={theme.expandToggle}
                      aria-expanded={isExpanded}
                      aria-label={isExpanded ? 'Collapse row' : 'Expand row'}
                      data-testid={`expand-toggle-${key}`}
                    >
                      {isExpanded ? '▼' : '▶'}
                    </button>
                  </td>
                );
              }

              if (col.field === '__actions' && rowActions) {
                return (
                  <td
                    key="__actions"
                    style={theme.bodyCell}
                    role="cell"
                    onClick={(e) => e.stopPropagation()}
                  >
                    {rowActions.map((action) => (
                      <button
                        key={action.label}
                        type="button"
                        style={theme.actionButton}
                        onClick={() => action.onClick(row)}
                        data-testid={`action-${action.label.toLowerCase()}`}
                      >
                        {action.label}
                      </button>
                    ))}
                  </td>
                );
              }

              const value = rowRecord[col.field];
              const isCellEditable =
                editable && editing.isColumnEditable(col.field);
              const isEditing =
                isCellEditable &&
                editing.editingCell?.rowKey === key &&
                editing.editingCell?.field === col.field;

              return (
                <td
                  key={col.field}
                  style={theme.bodyCell}
                  role="cell"
                  onDoubleClick={
                    isCellEditable
                      ? () => editing.startEditing(key, col.field)
                      : undefined
                  }
                >
                  {isEditing ? (
                    <input
                      type="text"
                      value={formatCellValue(
                        editing.getCellValue(key, col.field),
                      )}
                      onChange={(e) =>
                        editing.setCellValue(key, col.field, e.target.value)
                      }
                      // Blur and Enter both commit: clicking away from a cell
                      // the user has typed into must not discard the edit.
                      onBlur={() => {
                        void editing.commitCell();
                      }}
                      onKeyDown={(e) => {
                        if (e.key === 'Enter') {
                          e.preventDefault();
                          void editing.commitCell();
                        } else if (e.key === 'Escape') {
                          editing.discardRow(key);
                        }
                      }}
                      autoFocus
                      data-testid="edit-input"
                      style={{
                        width: '100%',
                        padding: '2px 4px',
                        border: '1px solid #3b82f6',
                        borderRadius: '2px',
                        outline: 'none',
                        fontSize: 'inherit',
                      }}
                    />
                  ) : renderCell ? (
                    renderCell(value, row, col)
                  ) : (
                    formatCellValue(value)
                  )}
                </td>
              );
            })}
          </tr>
        );

        rows.push(
          renderRow
            ? renderRow(row, rowIndex, defaultRowElement)
            : defaultRowElement,
        );

        if (isExpanded) {
          const childData = expansion.getChildData(key);
          rows.push(
            <tr
              key={`${key}-expanded`}
              style={theme.expandedRow}
              role="row"
              data-testid={`expanded-row-${key}`}
            >
              <td
                colSpan={visibleColumns.length}
                role="cell"
                style={theme.expandedRowContent}
              >
                {childData.loading ? (
                  <div
                    style={theme.childLoadingIndicator}
                    data-testid={`child-loading-${key}`}
                  >
                    Loading...
                  </div>
                ) : childData.error ? (
                  <div role="alert" data-testid={`child-error-${key}`}>
                    Error: {childData.error.message}
                  </div>
                ) : renderExpandedRow ? (
                  renderExpandedRow(row)
                ) : null}
              </td>
            </tr>,
          );
        }

        return rows;
      })}
    </>
  );
}

export interface BifrostTableProps<
  T = Record<string, unknown>,
> extends UseBifrostOptions {
  table: string;
  columns: ColumnConfig[];
  fields?: readonly FieldNameOf<T>[];
  theme?: AnyThemeName;
  customTheme?: TableTheme;
  themeOverrides?: Partial<TableTheme>;
  striped?: boolean;
  hoverable?: boolean;
  /**
   * Enables inline cell editing. Requires {@link onRowUpdate} — an editable
   * table with nowhere to write to would silently discard the user's typing.
   */
  editable?: boolean;
  /**
   * Persist each cell as it is committed. Defaults to `true`: this component
   * renders no explicit save control, so a deferred edit would sit dirty and
   * unsaved with nothing to flush it.
   */
  autoSave?: boolean;
  /** Persists a committed row edit. Required whenever `editable` is set. */
  onRowUpdate?: RowUpdateFn;
  /** Invoked when a row write rejects. The edit is kept for retry. */
  onSaveError?: SaveErrorFn;
  exportable?: boolean;
  expandable?: boolean;
  childQuery?: ChildQueryConfig;
  renderExpandedRow?: (row: T) => ReactNode;
  onRowClick?: (row: T) => void;
  rowActions?: RowAction<T>[];
  rowKey?: string;
  pagination?: PaginationConfig;
  defaultSort?: readonly SortOptionFor<T>[];
  /** Uncontrolled filter seed, read once on mount. */
  defaultFilter?: TableFilter;
  /**
   * Controlled filter. Re-seeds the table (and resets to the first page)
   * whenever its value changes, so a screen that swaps data sets does not keep
   * issuing the previous filter. Prefer this over remounting via `key`.
   */
  filter?: TableFilter;
  multiSort?: boolean;
  urlSync?: boolean | UseBifrostTableOptions['urlSync'];
  emptyMessage?: string;
  loadingMessage?: string;
  renderHeader?: (
    columns: ColumnConfig[],
    sortState: SortOption[],
  ) => ReactNode;
  renderFooter?: (
    data: T[],
    pagination: { page: number; pageSize: number },
  ) => ReactNode;
  renderRow?: (row: T, rowIndex: number, defaultRow: ReactNode) => ReactNode;
  renderCell?: (value: unknown, row: T, column: ColumnConfig) => ReactNode;
  renderEmpty?: () => ReactNode;
  renderLoading?: () => ReactNode;
  renderError?: (error: Error) => ReactNode;
  renderToolbar?: (actions: {
    export: () => void;
    expandAll: () => void;
    collapseAll: () => void;
  }) => ReactNode;
}

export function BifrostTable<T = Record<string, unknown>>(
  props: BifrostTableProps<T>,
) {
  const {
    table: tableName,
    columns,
    fields,
    theme: themeName = 'modern',
    customTheme,
    themeOverrides,
    striped = false,
    hoverable = true,
    editable = false,
    autoSave = true,
    onRowUpdate,
    onSaveError,
    exportable = false,
    expandable = false,
    childQuery,
    renderExpandedRow,
    onRowClick,
    rowActions,
    rowKey = 'id',
    pagination: paginationConfig,
    defaultSort,
    defaultFilter,
    filter,
    multiSort,
    urlSync,
    emptyMessage = 'No data available',
    loadingMessage = 'Loading...',
    renderHeader,
    renderFooter,
    renderRow,
    renderCell,
    renderEmpty,
    renderLoading,
    renderError,
    renderToolbar,
    ...bifrostOptions
  } = props;

  // An editable table with no write handler renders a working-looking input
  // whose contents go nowhere — the user believes the edit saved. Refuse the
  // configuration rather than ship a UI that quietly loses input.
  if (editable && !onRowUpdate) {
    throw new Error(
      'BifrostTable: `editable` requires `onRowUpdate` — without it, inline edits are silently discarded.',
    );
  }

  const resolvedTheme = customTheme ?? getTheme(themeName);
  const theme = mergeThemeOverrides(resolvedTheme, themeOverrides);

  const table = useBifrostTable<T>({
    table: tableName,
    columns,
    fields,
    pagination: paginationConfig,
    defaultSort,
    defaultFilter,
    filter,
    multiSort,
    rowKey,
    urlSync,
    expandable,
    childQuery,
    editable,
    autoSave,
    onRowUpdate,
    onSaveError,
    ...bifrostOptions,
  });

  const [hoveredRowIndex, setHoveredRowIndex] = useState<number | null>(null);

  const baseColumns = expandable
    ? [{ field: '__expand', header: '', width: 40 } as ColumnConfig, ...columns]
    : columns;

  const visibleColumns =
    rowActions && rowActions.length > 0
      ? [
          ...baseColumns,
          { field: '__actions', header: 'Actions' } as ColumnConfig,
        ]
      : baseColumns;

  const handleExport = useCallback(() => {
    downloadRowsAsCsv(table.data, columns, tableName);
  }, [table.data, columns, tableName]);

  const handleExpandAll = useCallback(() => {
    const allKeys = table.data.map((row) =>
      String((row as Record<string, unknown>)[rowKey]),
    );
    table.expansion.expandAll(allKeys);
    if (childQuery) {
      for (const row of table.data) {
        const rowRecord = row as Record<string, unknown>;
        const k = String(rowRecord[rowKey]);
        table.expansion.fetchChildData(k, rowRecord);
      }
    }
  }, [table.data, table.expansion, rowKey, childQuery]);

  const handleCollapseAll = useCallback(() => {
    table.expansion.collapseAll();
  }, [table.expansion]);

  if (table.error) {
    if (renderError) return <>{renderError(table.error)}</>;
    return (
      <div style={theme.errorContainer} role="alert">
        Error: {table.error.message}
      </div>
    );
  }

  const showPagination = table.data.length > 0 || table.pagination.page > 0;
  const showToolbar =
    (exportable && table.data.length > 0) ||
    (expandable && table.data.length > 0);

  return (
    <TableThemeContext.Provider value={{ theme }}>
      <div style={theme.container} data-testid="bifrost-table">
        {renderToolbar ? (
          renderToolbar({
            export: handleExport,
            expandAll: handleExpandAll,
            collapseAll: handleCollapseAll,
          })
        ) : showToolbar ? (
          <div
            style={{
              padding: '8px 16px',
              display: 'flex',
              justifyContent: 'flex-end',
              gap: '8px',
            }}
          >
            {expandable && table.data.length > 0 && (
              <>
                <button
                  type="button"
                  style={theme.actionButton}
                  onClick={handleExpandAll}
                  data-testid="expand-all-button"
                >
                  Expand All
                </button>
                <button
                  type="button"
                  style={theme.actionButton}
                  onClick={handleCollapseAll}
                  data-testid="collapse-all-button"
                >
                  Collapse All
                </button>
              </>
            )}
            {exportable && table.data.length > 0 && (
              <button
                type="button"
                style={theme.actionButton}
                onClick={handleExport}
                data-testid="export-button"
              >
                Export CSV
              </button>
            )}
          </div>
        ) : null}
        <table style={theme.table} role="table">
          {renderHeader ? (
            <TableHeader
              columns={visibleColumns}
              sortState={table.sorting.current}
            >
              <>{renderHeader(visibleColumns, table.sorting.current)}</>
            </TableHeader>
          ) : (
            <TableHeader
              columns={visibleColumns}
              sortState={table.sorting.current}
              onSort={table.sorting.toggleSort}
            />
          )}
          <tbody>
            {table.data.length === 0 && !table.loading ? (
              <tr role="row">
                <td
                  colSpan={visibleColumns.length}
                  style={theme.emptyContainer}
                  role="cell"
                >
                  {renderEmpty ? renderEmpty() : emptyMessage}
                </td>
              </tr>
            ) : (
              <TableRows<T>
                data={table.data}
                visibleColumns={visibleColumns}
                rowKey={rowKey}
                striped={striped}
                hoverable={hoverable}
                editable={editable}
                expandable={expandable}
                childQuery={childQuery}
                expansion={table.expansion}
                onRowClick={onRowClick}
                rowActions={rowActions}
                renderRow={renderRow}
                renderCell={renderCell}
                renderExpandedRow={renderExpandedRow}
                hoveredRowIndex={hoveredRowIndex}
                setHoveredRowIndex={setHoveredRowIndex}
                editing={table.editing}
              />
            )}
          </tbody>
        </table>
        {showPagination && (
          <Pagination
            page={table.pagination.page}
            pageSize={table.pagination.pageSize}
            dataLength={table.data.length}
            onPrevious={table.pagination.previousPage}
            onNext={table.pagination.nextPage}
          />
        )}
        {renderFooter && (
          <div data-testid="table-footer">
            {renderFooter(table.data, {
              page: table.pagination.page,
              pageSize: table.pagination.pageSize,
            })}
          </div>
        )}
        {table.loading && (
          <div style={theme.loadingOverlay} data-testid="loading-overlay">
            {renderLoading ? renderLoading() : loadingMessage}
          </div>
        )}
      </div>
    </TableThemeContext.Provider>
  );
}

function mergeThemeOverrides(
  theme: TableTheme,
  overrides: Partial<TableTheme> | undefined,
): TableTheme {
  if (!overrides) return theme;
  const result = { ...theme };
  for (const key of Object.keys(overrides) as (keyof TableTheme)[]) {
    const override = overrides[key];
    if (override) {
      result[key] = { ...theme[key], ...override };
    }
  }
  return result;
}
