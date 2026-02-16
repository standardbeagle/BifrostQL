import { useState, useCallback } from 'react';
import type { CSSProperties, ReactNode } from 'react';
import { useBifrostTable } from '../hooks/use-bifrost-table';
import type {
  ColumnConfig,
  PaginationConfig,
  UseBifrostTableOptions,
} from '../hooks/use-bifrost-table';
import type { UseBifrostOptions } from '../hooks/use-bifrost';
import type { SortOption, TableFilter } from '../types';
import { getTheme } from './table-theme';
import type { ThemeName, TableTheme } from './table-theme';

export interface RowAction<T = Record<string, unknown>> {
  label: string;
  onClick: (row: T) => void;
}

export interface BifrostTableProps<
  T = Record<string, unknown>,
> extends UseBifrostOptions {
  query: string;
  columns: ColumnConfig[];
  fields?: string[];
  theme?: ThemeName;
  themeOverrides?: Partial<TableTheme>;
  striped?: boolean;
  hoverable?: boolean;
  editable?: boolean;
  exportable?: boolean;
  onRowClick?: (row: T) => void;
  rowActions?: RowAction<T>[];
  rowKey?: string;
  pagination?: PaginationConfig;
  defaultSort?: SortOption[];
  defaultFilters?: TableFilter;
  multiSort?: boolean;
  urlSync?: boolean | UseBifrostTableOptions['urlSync'];
  emptyMessage?: string;
  loadingMessage?: string;
  renderCell?: (value: unknown, row: T, column: ColumnConfig) => ReactNode;
  renderEmpty?: () => ReactNode;
  renderLoading?: () => ReactNode;
  renderError?: (error: Error) => ReactNode;
}

function formatCellValue(value: unknown): string {
  if (value === null || value === undefined) return '';
  if (typeof value === 'boolean') return value ? 'Yes' : 'No';
  if (value instanceof Date) return value.toLocaleDateString();
  return String(value);
}

function exportToCsv<T>(
  data: T[],
  columns: ColumnConfig[],
  query: string,
): void {
  const headers = columns.map((c) => c.header);
  const rows = data.map((row) =>
    columns.map((col) => {
      const val = (row as Record<string, unknown>)[col.field];
      const str = formatCellValue(val);
      return str.includes(',') || str.includes('"')
        ? `"${str.replace(/"/g, '""')}"`
        : str;
    }),
  );

  const csv = [headers.join(','), ...rows.map((r) => r.join(','))].join('\n');
  const blob = new Blob([csv], { type: 'text/csv;charset=utf-8;' });
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = `${query}-export.csv`;
  link.click();
  URL.revokeObjectURL(url);
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

export function BifrostTable<T = Record<string, unknown>>(
  props: BifrostTableProps<T>,
) {
  const {
    query,
    columns,
    fields,
    theme: themeName = 'modern',
    themeOverrides,
    striped = false,
    hoverable = true,
    editable = false,
    exportable = false,
    onRowClick,
    rowActions,
    rowKey = 'id',
    pagination: paginationConfig,
    defaultSort,
    defaultFilters,
    multiSort,
    urlSync,
    emptyMessage = 'No data available',
    loadingMessage = 'Loading...',
    renderCell,
    renderEmpty,
    renderLoading,
    renderError,
    ...bifrostOptions
  } = props;

  const theme = applyOverrides(getTheme(themeName), themeOverrides);

  const table = useBifrostTable<T>({
    query,
    columns,
    fields,
    pagination: paginationConfig,
    defaultSort,
    defaultFilters,
    multiSort,
    rowKey,
    urlSync,
    ...bifrostOptions,
  });

  const [hoveredRowIndex, setHoveredRowIndex] = useState<number | null>(null);
  const [editingCell, setEditingCell] = useState<{
    rowIndex: number;
    field: string;
  } | null>(null);
  const [editValue, setEditValue] = useState('');

  const visibleColumns =
    rowActions && rowActions.length > 0
      ? [...columns, { field: '__actions', header: 'Actions' } as ColumnConfig]
      : columns;

  const handleExport = useCallback(() => {
    exportToCsv(table.data, columns, query);
  }, [table.data, columns, query]);

  const handleEditStart = useCallback(
    (rowIndex: number, field: string, currentValue: unknown) => {
      if (!editable) return;
      setEditingCell({ rowIndex, field });
      setEditValue(formatCellValue(currentValue));
    },
    [editable],
  );

  const handleEditCancel = useCallback(() => {
    setEditingCell(null);
    setEditValue('');
  }, []);

  if (table.error) {
    if (renderError) return <>{renderError(table.error)}</>;
    return (
      <div style={theme.errorContainer} role="alert">
        Error: {table.error.message}
      </div>
    );
  }

  const showPagination = table.data.length > 0 || table.pagination.page > 0;

  return (
    <div style={theme.container} data-testid="bifrost-table">
      {exportable && table.data.length > 0 && (
        <div
          style={{
            padding: '8px 16px',
            display: 'flex',
            justifyContent: 'flex-end',
          }}
        >
          <button
            type="button"
            style={theme.actionButton}
            onClick={handleExport}
            data-testid="export-button"
          >
            Export CSV
          </button>
        </div>
      )}
      <table style={theme.table} role="table">
        <thead>
          <tr style={theme.headerRow} role="row">
            {visibleColumns.map((col) => {
              const sortDir = getSortDirection(
                table.sorting.current,
                col.field,
              );
              const isSortable = col.sortable && col.field !== '__actions';
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
                    isSortable
                      ? () => table.sorting.toggleSort(col.field)
                      : undefined
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
            table.data.map((row, rowIndex) => {
              const rowRecord = row as Record<string, unknown>;
              const key = String(rowRecord[rowKey] ?? rowIndex);
              const isHovered = hoverable && hoveredRowIndex === rowIndex;
              const isStriped = striped && rowIndex % 2 === 1;

              const rowStyle: CSSProperties = {
                ...theme.bodyRow,
                ...(isStriped ? theme.bodyRowStriped : {}),
                ...(isHovered ? theme.bodyRowHover : {}),
                ...(onRowClick ? { cursor: 'pointer' } : {}),
              };

              return (
                <tr
                  key={key}
                  style={rowStyle}
                  role="row"
                  onClick={onRowClick ? () => onRowClick(row) : undefined}
                  onMouseEnter={
                    hoverable ? () => setHoveredRowIndex(rowIndex) : undefined
                  }
                  onMouseLeave={
                    hoverable ? () => setHoveredRowIndex(null) : undefined
                  }
                  data-testid={`table-row-${key}`}
                >
                  {visibleColumns.map((col) => {
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
                    const isEditing =
                      editable &&
                      editingCell?.rowIndex === rowIndex &&
                      editingCell?.field === col.field;

                    return (
                      <td
                        key={col.field}
                        style={theme.bodyCell}
                        role="cell"
                        onDoubleClick={
                          editable
                            ? () => handleEditStart(rowIndex, col.field, value)
                            : undefined
                        }
                      >
                        {isEditing ? (
                          <input
                            type="text"
                            value={editValue}
                            onChange={(e) => setEditValue(e.target.value)}
                            onBlur={handleEditCancel}
                            onKeyDown={(e) => {
                              if (e.key === 'Escape') handleEditCancel();
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
            })
          )}
        </tbody>
      </table>
      {showPagination && (
        <div style={theme.pagination} data-testid="pagination">
          <button
            type="button"
            style={
              table.pagination.page === 0
                ? theme.paginationButtonDisabled
                : theme.paginationButton
            }
            disabled={table.pagination.page === 0}
            onClick={table.pagination.previousPage}
            data-testid="pagination-prev"
          >
            Previous
          </button>
          <span style={theme.paginationInfo} data-testid="pagination-info">
            Page {table.pagination.page + 1}
          </span>
          <button
            type="button"
            style={
              table.data.length < table.pagination.pageSize
                ? theme.paginationButtonDisabled
                : theme.paginationButton
            }
            disabled={table.data.length < table.pagination.pageSize}
            onClick={table.pagination.nextPage}
            data-testid="pagination-next"
          >
            Next
          </button>
        </div>
      )}
      {table.loading && (
        <div style={theme.loadingOverlay} data-testid="loading-overlay">
          {renderLoading ? renderLoading() : loadingMessage}
        </div>
      )}
    </div>
  );
}

function applyOverrides(
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
