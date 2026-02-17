import type { CSSProperties } from 'react';

export type ThemeName = 'modern' | 'classic' | 'minimal' | 'dense';

export type DarkThemeName = `${ThemeName}-dark`;

export type AnyThemeName = ThemeName | DarkThemeName;

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
  expandToggle: CSSProperties;
  expandedRow: CSSProperties;
  expandedRowContent: CSSProperties;
  childLoadingIndicator: CSSProperties;
  toolbar: CSSProperties;
  toolbarButton: CSSProperties;
}

export interface ThemeTokens {
  fontFamily?: string;
  fontSize?: string;
  borderRadius?: string;
  borderColor?: string;
  headerBg?: string;
  headerColor?: string;
  headerBorderColor?: string;
  bodyBg?: string;
  bodyColor?: string;
  bodyBorderColor?: string;
  hoverBg?: string;
  stripedBg?: string;
  accentColor?: string;
  errorColor?: string;
  mutedColor?: string;
  surfaceBg?: string;
  overlayBg?: string;
  buttonBg?: string;
  buttonColor?: string;
  buttonBorder?: string;
  disabledBg?: string;
  disabledColor?: string;
  disabledBorder?: string;
  padding?: string;
  densePadding?: string;
  shadow?: string;
}

const defaultTokens: Required<ThemeTokens> = {
  fontFamily:
    '-apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif',
  fontSize: '14px',
  borderRadius: '4px',
  borderColor: '#e5e7eb',
  headerBg: 'transparent',
  headerColor: '#374151',
  headerBorderColor: '#e2e8f0',
  bodyBg: 'transparent',
  bodyColor: '#1f2937',
  bodyBorderColor: '#e5e7eb',
  hoverBg: '#f9fafb',
  stripedBg: '#f8fafc',
  accentColor: '#3b82f6',
  errorColor: '#dc2626',
  mutedColor: '#9ca3af',
  surfaceBg: '#fff',
  overlayBg: 'rgba(255, 255, 255, 0.7)',
  buttonBg: '#fff',
  buttonColor: '#374151',
  buttonBorder: '#d1d5db',
  disabledBg: '#f9fafb',
  disabledColor: '#9ca3af',
  disabledBorder: '#e5e7eb',
  padding: '12px 16px',
  densePadding: '4px 8px',
  shadow: 'none',
};

function buildThemeFromTokens(tokens: Required<ThemeTokens>): TableTheme {
  return {
    container: {
      position: 'relative',
      width: '100%',
      overflowX: 'auto',
      boxShadow: tokens.shadow !== 'none' ? tokens.shadow : undefined,
    },
    table: {
      width: '100%',
      borderCollapse: 'collapse',
      fontFamily: tokens.fontFamily,
      fontSize: tokens.fontSize,
    },
    headerRow: {
      backgroundColor: tokens.headerBg,
      borderBottom: `2px solid ${tokens.headerBorderColor}`,
    },
    headerCell: {
      padding: tokens.padding,
      textAlign: 'left',
      fontWeight: 600,
      color: tokens.headerColor,
      userSelect: 'none',
      whiteSpace: 'nowrap',
    },
    bodyRow: {
      backgroundColor: tokens.bodyBg,
      borderBottom: `1px solid ${tokens.bodyBorderColor}`,
      transition: 'background-color 0.15s ease',
    },
    bodyRowHover: {
      backgroundColor: tokens.hoverBg,
    },
    bodyRowStriped: {
      backgroundColor: tokens.stripedBg,
    },
    bodyCell: {
      padding: tokens.padding,
      color: tokens.bodyColor,
    },
    pagination: {
      display: 'flex',
      alignItems: 'center',
      justifyContent: 'space-between',
      padding: tokens.padding,
      borderTop: `1px solid ${tokens.borderColor}`,
      fontSize: tokens.fontSize,
      color: tokens.mutedColor,
    },
    paginationButton: {
      padding: '6px 12px',
      border: `1px solid ${tokens.buttonBorder}`,
      borderRadius: tokens.borderRadius,
      background: tokens.buttonBg,
      cursor: 'pointer',
      fontSize: '13px',
      color: tokens.buttonColor,
    },
    paginationButtonDisabled: {
      padding: '6px 12px',
      border: `1px solid ${tokens.disabledBorder}`,
      borderRadius: tokens.borderRadius,
      background: tokens.disabledBg,
      cursor: 'not-allowed',
      fontSize: '13px',
      color: tokens.disabledColor,
    },
    paginationInfo: {
      fontSize: '13px',
      color: tokens.mutedColor,
    },
    loadingOverlay: {
      position: 'absolute',
      inset: 0,
      display: 'flex',
      alignItems: 'center',
      justifyContent: 'center',
      backgroundColor: tokens.overlayBg,
      fontSize: tokens.fontSize,
      color: tokens.mutedColor,
    },
    errorContainer: {
      padding: '24px',
      textAlign: 'center',
      color: tokens.errorColor,
      fontSize: tokens.fontSize,
    },
    emptyContainer: {
      padding: '24px',
      textAlign: 'center',
      color: tokens.mutedColor,
      fontSize: tokens.fontSize,
    },
    actionButton: {
      padding: '4px 8px',
      border: `1px solid ${tokens.buttonBorder}`,
      borderRadius: tokens.borderRadius,
      background: tokens.buttonBg,
      cursor: 'pointer',
      fontSize: '12px',
      color: tokens.buttonColor,
      marginRight: '4px',
    },
    sortIndicator: {
      marginLeft: '4px',
      fontSize: '12px',
      color: tokens.mutedColor,
    },
    expandToggle: {
      background: 'none',
      border: 'none',
      cursor: 'pointer',
      padding: '2px 6px',
      fontSize: '14px',
      color: tokens.mutedColor,
      lineHeight: 1,
    },
    expandedRow: {
      backgroundColor: tokens.stripedBg,
    },
    expandedRowContent: {
      padding: '12px 16px 12px 48px',
    },
    childLoadingIndicator: {
      padding: '12px 16px 12px 48px',
      color: tokens.mutedColor,
      fontSize: '13px',
    },
    toolbar: {
      padding: '8px 16px',
      display: 'flex',
      justifyContent: 'flex-end',
      gap: '8px',
      borderBottom: `1px solid ${tokens.borderColor}`,
    },
    toolbarButton: {
      padding: '4px 8px',
      border: `1px solid ${tokens.buttonBorder}`,
      borderRadius: tokens.borderRadius,
      background: tokens.buttonBg,
      cursor: 'pointer',
      fontSize: '12px',
      color: tokens.buttonColor,
    },
  };
}

const baseTheme: TableTheme = buildThemeFromTokens(defaultTokens);

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

const darkTokens: Required<ThemeTokens> = {
  fontFamily: defaultTokens.fontFamily,
  fontSize: '14px',
  borderRadius: '4px',
  borderColor: '#374151',
  headerBg: '#1f2937',
  headerColor: '#e5e7eb',
  headerBorderColor: '#4b5563',
  bodyBg: '#111827',
  bodyColor: '#e5e7eb',
  bodyBorderColor: '#374151',
  hoverBg: '#1f2937',
  stripedBg: '#1a2332',
  accentColor: '#60a5fa',
  errorColor: '#f87171',
  mutedColor: '#9ca3af',
  surfaceBg: '#1f2937',
  overlayBg: 'rgba(17, 24, 39, 0.7)',
  buttonBg: '#1f2937',
  buttonColor: '#e5e7eb',
  buttonBorder: '#4b5563',
  disabledBg: '#1f2937',
  disabledColor: '#6b7280',
  disabledBorder: '#374151',
  padding: '12px 16px',
  densePadding: '4px 8px',
  shadow: 'none',
};

const baseDarkTheme: TableTheme = buildThemeFromTokens(darkTokens);

const modernDarkTheme: TableTheme = {
  ...baseDarkTheme,
  container: {
    ...baseDarkTheme.container,
    borderRadius: '8px',
    boxShadow:
      '0 1px 3px rgba(0, 0, 0, 0.3), 0 1px 2px rgba(0, 0, 0, 0.2)',
    border: '1px solid #374151',
    overflowY: 'hidden',
    backgroundColor: '#111827',
  },
  headerRow: {
    backgroundColor: '#1f2937',
    borderBottom: '2px solid #4b5563',
  },
  bodyRowHover: {
    backgroundColor: '#1e3a5f',
  },
  paginationButton: {
    ...baseDarkTheme.paginationButton,
    borderRadius: '6px',
  },
  actionButton: {
    ...baseDarkTheme.actionButton,
    borderRadius: '6px',
  },
};

const classicDarkTheme: TableTheme = {
  ...baseDarkTheme,
  container: {
    ...baseDarkTheme.container,
    border: '1px solid #4b5563',
    backgroundColor: '#111827',
  },
  headerRow: {
    backgroundColor: '#1f2937',
    borderBottom: '2px solid #6b7280',
  },
  headerCell: {
    ...baseDarkTheme.headerCell,
    borderRight: '1px solid #4b5563',
  },
  bodyRow: {
    borderBottom: '1px solid #4b5563',
  },
  bodyCell: {
    ...baseDarkTheme.bodyCell,
    borderRight: '1px solid #374151',
  },
};

const minimalDarkTheme: TableTheme = {
  ...baseDarkTheme,
  container: {
    ...baseDarkTheme.container,
    backgroundColor: '#111827',
  },
  headerRow: {
    borderBottom: '1px solid #374151',
  },
  headerCell: {
    ...baseDarkTheme.headerCell,
    fontWeight: 500,
    color: '#9ca3af',
    fontSize: '12px',
    textTransform: 'uppercase',
    letterSpacing: '0.05em',
  },
  bodyRow: {
    borderBottom: '1px solid #1f2937',
  },
  bodyRowHover: {
    backgroundColor: '#1a1a2e',
  },
  bodyRowStriped: {
    backgroundColor: '#1a1a2e',
  },
  pagination: {
    ...baseDarkTheme.pagination,
    borderTop: '1px solid #1f2937',
  },
  paginationButton: {
    ...baseDarkTheme.paginationButton,
    border: 'none',
    background: 'transparent',
    color: '#9ca3af',
  },
  paginationButtonDisabled: {
    ...baseDarkTheme.paginationButtonDisabled,
    border: 'none',
    background: 'transparent',
  },
};

const denseDarkTheme: TableTheme = {
  ...baseDarkTheme,
  container: {
    ...baseDarkTheme.container,
    border: '1px solid #374151',
    backgroundColor: '#111827',
  },
  table: {
    ...baseDarkTheme.table,
    fontSize: '12px',
  },
  headerCell: {
    ...baseDarkTheme.headerCell,
    padding: '6px 8px',
    fontSize: '12px',
  },
  bodyCell: {
    ...baseDarkTheme.bodyCell,
    padding: '4px 8px',
    fontSize: '12px',
  },
  pagination: {
    ...baseDarkTheme.pagination,
    padding: '6px 8px',
    fontSize: '12px',
  },
  paginationButton: {
    ...baseDarkTheme.paginationButton,
    padding: '3px 8px',
    fontSize: '11px',
  },
  paginationButtonDisabled: {
    ...baseDarkTheme.paginationButtonDisabled,
    padding: '3px 8px',
    fontSize: '11px',
  },
  actionButton: {
    ...baseDarkTheme.actionButton,
    padding: '2px 6px',
    fontSize: '11px',
  },
};

const themes: Record<AnyThemeName, TableTheme> = {
  modern: modernTheme,
  classic: classicTheme,
  minimal: minimalTheme,
  dense: denseTheme,
  'modern-dark': modernDarkTheme,
  'classic-dark': classicDarkTheme,
  'minimal-dark': minimalDarkTheme,
  'dense-dark': denseDarkTheme,
};

export function getTheme(name: AnyThemeName): TableTheme {
  return themes[name];
}

export function createBifrostTheme(tokens: ThemeTokens): TableTheme {
  const merged: Required<ThemeTokens> = { ...defaultTokens, ...tokens };
  return buildThemeFromTokens(merged);
}

export function getThemeTokens(name: AnyThemeName): Required<ThemeTokens> {
  const isDark = name.endsWith('-dark');
  return isDark ? { ...darkTokens } : { ...defaultTokens };
}

export function themeToCssVariables(
  theme: TableTheme,
  prefix = '--bfq',
): Record<string, string> {
  const vars: Record<string, string> = {};

  function extractColors(obj: CSSProperties, path: string) {
    for (const [key, value] of Object.entries(obj)) {
      if (typeof value === 'string') {
        vars[`${prefix}-${path}-${camelToKebab(key)}`] = value;
      }
    }
  }

  for (const [section, styles] of Object.entries(theme)) {
    extractColors(styles as CSSProperties, camelToKebab(section));
  }

  return vars;
}

function camelToKebab(str: string): string {
  return str.replace(/([A-Z])/g, '-$1').toLowerCase();
}
