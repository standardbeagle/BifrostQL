import type { CSSProperties } from 'react';

export type ThemeName = 'modern' | 'classic' | 'minimal' | 'dense';

export interface TableTheme {
  container: CSSProperties;
  table: CSSProperties;
  headerRow: CSSProperties;
  headerCell: CSSProperties;
  bodyRow: CSSProperties;
  bodyRowHover: CSSProperties;
  bodyRowStriped: CSSProperties;
  bodyCell: CSSProperties;
  pagination: CSSProperties;
  paginationButton: CSSProperties;
  paginationButtonDisabled: CSSProperties;
  paginationInfo: CSSProperties;
  loadingOverlay: CSSProperties;
  errorContainer: CSSProperties;
  emptyContainer: CSSProperties;
  actionButton: CSSProperties;
  sortIndicator: CSSProperties;
}

const baseTheme: TableTheme = {
  container: {
    position: 'relative',
    width: '100%',
    overflowX: 'auto',
  },
  table: {
    width: '100%',
    borderCollapse: 'collapse',
    fontFamily:
      '-apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif',
    fontSize: '14px',
  },
  headerRow: {
    borderBottom: '2px solid #e2e8f0',
  },
  headerCell: {
    padding: '12px 16px',
    textAlign: 'left',
    fontWeight: 600,
    color: '#374151',
    userSelect: 'none',
    whiteSpace: 'nowrap',
  },
  bodyRow: {
    borderBottom: '1px solid #e5e7eb',
    transition: 'background-color 0.15s ease',
  },
  bodyRowHover: {
    backgroundColor: '#f9fafb',
  },
  bodyRowStriped: {
    backgroundColor: '#f8fafc',
  },
  bodyCell: {
    padding: '12px 16px',
    color: '#1f2937',
  },
  pagination: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    padding: '12px 16px',
    borderTop: '1px solid #e5e7eb',
    fontSize: '14px',
    color: '#6b7280',
  },
  paginationButton: {
    padding: '6px 12px',
    border: '1px solid #d1d5db',
    borderRadius: '4px',
    background: '#fff',
    cursor: 'pointer',
    fontSize: '13px',
    color: '#374151',
  },
  paginationButtonDisabled: {
    padding: '6px 12px',
    border: '1px solid #e5e7eb',
    borderRadius: '4px',
    background: '#f9fafb',
    cursor: 'not-allowed',
    fontSize: '13px',
    color: '#9ca3af',
  },
  paginationInfo: {
    fontSize: '13px',
    color: '#6b7280',
  },
  loadingOverlay: {
    position: 'absolute',
    inset: 0,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: 'rgba(255, 255, 255, 0.7)',
    fontSize: '14px',
    color: '#6b7280',
  },
  errorContainer: {
    padding: '24px',
    textAlign: 'center',
    color: '#dc2626',
    fontSize: '14px',
  },
  emptyContainer: {
    padding: '24px',
    textAlign: 'center',
    color: '#9ca3af',
    fontSize: '14px',
  },
  actionButton: {
    padding: '4px 8px',
    border: '1px solid #d1d5db',
    borderRadius: '4px',
    background: '#fff',
    cursor: 'pointer',
    fontSize: '12px',
    color: '#374151',
    marginRight: '4px',
  },
  sortIndicator: {
    marginLeft: '4px',
    fontSize: '12px',
    color: '#9ca3af',
  },
};

const modernTheme: TableTheme = {
  ...baseTheme,
  container: {
    ...baseTheme.container,
    borderRadius: '8px',
    boxShadow: '0 1px 3px rgba(0, 0, 0, 0.1), 0 1px 2px rgba(0, 0, 0, 0.06)',
    border: '1px solid #e5e7eb',
    overflowY: 'hidden',
  },
  headerRow: {
    backgroundColor: '#f9fafb',
    borderBottom: '2px solid #e5e7eb',
  },
  bodyRowHover: {
    backgroundColor: '#eff6ff',
  },
  paginationButton: {
    ...baseTheme.paginationButton,
    borderRadius: '6px',
  },
  actionButton: {
    ...baseTheme.actionButton,
    borderRadius: '6px',
  },
};

const classicTheme: TableTheme = {
  ...baseTheme,
  container: {
    ...baseTheme.container,
    border: '1px solid #d1d5db',
  },
  table: {
    ...baseTheme.table,
    borderCollapse: 'collapse',
  },
  headerRow: {
    backgroundColor: '#f3f4f6',
    borderBottom: '2px solid #9ca3af',
  },
  headerCell: {
    ...baseTheme.headerCell,
    borderRight: '1px solid #d1d5db',
  },
  bodyRow: {
    borderBottom: '1px solid #d1d5db',
  },
  bodyCell: {
    ...baseTheme.bodyCell,
    borderRight: '1px solid #e5e7eb',
  },
};

const minimalTheme: TableTheme = {
  ...baseTheme,
  headerRow: {
    borderBottom: '1px solid #e5e7eb',
  },
  headerCell: {
    ...baseTheme.headerCell,
    fontWeight: 500,
    color: '#6b7280',
    fontSize: '12px',
    textTransform: 'uppercase',
    letterSpacing: '0.05em',
  },
  bodyRow: {
    borderBottom: '1px solid #f3f4f6',
  },
  bodyRowHover: {
    backgroundColor: '#fafafa',
  },
  bodyRowStriped: {
    backgroundColor: '#fafafa',
  },
  pagination: {
    ...baseTheme.pagination,
    borderTop: '1px solid #f3f4f6',
  },
  paginationButton: {
    ...baseTheme.paginationButton,
    border: 'none',
    background: 'transparent',
    color: '#6b7280',
  },
  paginationButtonDisabled: {
    ...baseTheme.paginationButtonDisabled,
    border: 'none',
    background: 'transparent',
  },
};

const denseTheme: TableTheme = {
  ...baseTheme,
  container: {
    ...baseTheme.container,
    border: '1px solid #e5e7eb',
  },
  table: {
    ...baseTheme.table,
    fontSize: '12px',
  },
  headerCell: {
    ...baseTheme.headerCell,
    padding: '6px 8px',
    fontSize: '12px',
  },
  bodyCell: {
    ...baseTheme.bodyCell,
    padding: '4px 8px',
    fontSize: '12px',
  },
  pagination: {
    ...baseTheme.pagination,
    padding: '6px 8px',
    fontSize: '12px',
  },
  paginationButton: {
    ...baseTheme.paginationButton,
    padding: '3px 8px',
    fontSize: '11px',
  },
  paginationButtonDisabled: {
    ...baseTheme.paginationButtonDisabled,
    padding: '3px 8px',
    fontSize: '11px',
  },
  actionButton: {
    ...baseTheme.actionButton,
    padding: '2px 6px',
    fontSize: '11px',
  },
};

const themes: Record<ThemeName, TableTheme> = {
  modern: modernTheme,
  classic: classicTheme,
  minimal: minimalTheme,
  dense: denseTheme,
};

export function getTheme(name: ThemeName): TableTheme {
  return themes[name];
}
