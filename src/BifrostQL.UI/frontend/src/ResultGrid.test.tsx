// @vitest-environment jsdom
/**
 * The SQL console result block exposes CSV/JSON export wired to the SAME shared
 * util the edit-db grid uses (`@standardbeagle/edit-db`), not a second console-
 * local implementation. These tests pin that the buttons serialize the console's
 * in-memory columnar rows through that util and hand the result to its
 * downloader with the right filename + MIME.
 */
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';

const exportUtil = vi.hoisted(() => ({
    buildCsv: vi.fn(() => 'CSV_BYTES'),
    buildJson: vi.fn(() => 'JSON_BYTES'),
    downloadTextFile: vi.fn(),
    filenameFor: vi.fn((base: string, format: string) => `${base}-export.${format}`),
    mimeFor: vi.fn((format: string) =>
        format === 'csv' ? 'text/csv;charset=utf-8;' : 'application/json;charset=utf-8;',
    ),
}));

vi.mock('@standardbeagle/edit-db', () => exportUtil);

import { ResultGrid } from './ResultGrid';

const columns = [
    { name: 'id', type: 'int' },
    { name: 'name', type: 'text' },
];
const rows = [
    [1, 'Ada'],
    [2, 'Grace'],
];

afterEach(() => {
    cleanup();
    vi.clearAllMocks();
});

describe('ResultGrid export', () => {
    it('serializes rows to CSV through the shared util and downloads them', () => {
        render(<ResultGrid columns={columns} rows={rows} />);
        fireEvent.click(screen.getByRole('button', { name: /export csv/i }));

        expect(exportUtil.buildCsv).toHaveBeenCalledWith(
            ['id', 'name'],
            rows,
            expect.objectContaining({ bom: true }),
        );
        expect(exportUtil.downloadTextFile).toHaveBeenCalledWith(
            'CSV_BYTES',
            'query-result-export.csv',
            expect.stringContaining('text/csv'),
        );
    });

    it('serializes rows to JSON through the shared util', () => {
        render(<ResultGrid columns={columns} rows={rows} />);
        fireEvent.click(screen.getByRole('button', { name: /export json/i }));

        expect(exportUtil.buildJson).toHaveBeenCalledWith(['id', 'name'], rows);
        expect(exportUtil.downloadTextFile).toHaveBeenCalledWith(
            'JSON_BYTES',
            'query-result-export.json',
            expect.stringContaining('application/json'),
        );
    });

    it('names unnamed columns positionally in the export header', () => {
        render(
            <ResultGrid
                columns={[{ name: '', type: 'int' }, { name: 'name', type: 'text' }]}
                rows={rows}
            />,
        );
        fireEvent.click(screen.getByRole('button', { name: /export csv/i }));
        expect(exportUtil.buildCsv).toHaveBeenCalledWith(
            ['col1', 'name'],
            rows,
            expect.objectContaining({ bom: true }),
        );
    });

    it('disables export when there are no rows', () => {
        render(<ResultGrid columns={columns} rows={[]} />);
        const btn = screen.getByRole('button', { name: /export csv/i }) as HTMLButtonElement;
        expect(btn.disabled).toBe(true);
    });
});
