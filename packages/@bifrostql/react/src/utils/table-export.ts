import type { ExportFormatter } from '../hooks/use-bifrost-table.types';

export function escapeCsvValue(value: unknown): string {
  if (value === null || value === undefined) return '';
  const str = String(value);
  if (
    str.includes(',') ||
    str.includes('"') ||
    str.includes('\n') ||
    str.includes('\r')
  ) {
    return `"${str.replace(/"/g, '""')}"`;
  }
  return str;
}

export function formatExportValue(
  value: unknown,
  field: string,
  row: Record<string, unknown>,
  formatters?: Record<string, ExportFormatter>,
): string {
  if (formatters?.[field]) {
    return formatters[field](value, field, row);
  }
  if (value === null || value === undefined) return '';
  return String(value);
}

export function rowsToCsv(
  rows: Record<string, unknown>[],
  fields: string[],
  headers: string[],
  formatters?: Record<string, ExportFormatter>,
): string {
  const headerLine = headers.map(escapeCsvValue).join(',');
  const dataLines = rows.map((row) =>
    fields
      .map((field) =>
        escapeCsvValue(formatExportValue(row[field], field, row, formatters)),
      )
      .join(','),
  );
  return [headerLine, ...dataLines].join('\n');
}

export function rowsToTsv(
  rows: Record<string, unknown>[],
  fields: string[],
  headers: string[],
  formatters?: Record<string, ExportFormatter>,
): string {
  const headerLine = headers.join('\t');
  const dataLines = rows.map((row) =>
    fields
      .map((field) =>
        formatExportValue(row[field], field, row, formatters).replace(
          /\t/g,
          ' ',
        ),
      )
      .join('\t'),
  );
  return [headerLine, ...dataLines].join('\n');
}

export function rowsToJson(
  rows: Record<string, unknown>[],
  fields: string[],
  formatters?: Record<string, ExportFormatter>,
): string {
  const filtered = rows.map((row) => {
    const obj: Record<string, unknown> = {};
    for (const field of fields) {
      if (formatters?.[field]) {
        obj[field] = formatters[field](row[field], field, row);
      } else {
        obj[field] = row[field];
      }
    }
    return obj;
  });
  return JSON.stringify(filtered, null, 2);
}

export function triggerDownload(
  content: string,
  filename: string,
  mimeType: string,
): void {
  if (typeof window === 'undefined' || typeof document === 'undefined') return;
  const blob = new Blob([content], { type: mimeType });
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = filename;
  document.body.appendChild(link);
  link.click();
  document.body.removeChild(link);
  URL.revokeObjectURL(url);
}
