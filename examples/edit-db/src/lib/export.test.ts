import { describe, it, expect, vi } from 'vitest';
import {
    formatCsvCell,
    buildCsv,
    buildJson,
    exportAllRows,
    UTF8_BOM,
    type ExportPage,
} from './export';

describe('formatCsvCell', () => {
    it('leaves a plain value unquoted', () => {
        expect(formatCsvCell('abc')).toBe('abc');
        expect(formatCsvCell(42)).toBe('42');
        expect(formatCsvCell(true)).toBe('true');
    });

    it('emits NULL as an empty field but empty string as a quoted empty pair', () => {
        // NULL vs empty string must be distinguishable in the bytes: NULL -> nothing,
        // empty string -> "" (PostgreSQL CSV convention).
        expect(formatCsvCell(null)).toBe('');
        expect(formatCsvCell(undefined)).toBe('');
        expect(formatCsvCell('')).toBe('""');
    });

    it('quotes and escapes RFC4180 special characters', () => {
        expect(formatCsvCell('x,y')).toBe('"x,y"');
        expect(formatCsvCell('he said "hi"')).toBe('"he said ""hi"""');
        expect(formatCsvCell('line1\nline2')).toBe('"line1\nline2"');
        expect(formatCsvCell('a\r\nb')).toBe('"a\r\nb"');
    });

    it('renders a bigint as its exact decimal string', () => {
        expect(formatCsvCell(9007199254740993n)).toBe('9007199254740993');
    });

    it('renders a Date as a stable ISO-8601 string', () => {
        expect(formatCsvCell(new Date('2026-07-14T12:34:56.000Z'))).toBe(
            '2026-07-14T12:34:56.000Z',
        );
    });
});

describe('buildCsv', () => {
    it('produces RFC4180-correct bytes for a comma/quote/newline/NULL/empty row', () => {
        const headers = ['a', 'b', 'c', 'd', 'e'];
        const rows = [['x,y', 'he said "hi"', 'line1\nline2', null, '']];
        const csv = buildCsv(headers, rows);
        // Header row then one data row, CRLF-separated per RFC4180.
        expect(csv).toBe(
            'a,b,c,d,e\r\n' +
                '"x,y","he said ""hi""","line1\nline2",,""',
        );
        // NULL (4th field) is a bare empty; empty string (5th) is "" — distinguishable.
        const dataLine = csv.split('\r\n')[1];
        expect(dataLine.endsWith(',,""')).toBe(true);
    });

    it('prepends the UTF-8 BOM (EF BB BF) as the first bytes when requested', () => {
        const csv = buildCsv(['a'], [['1']], { bom: true });
        expect(csv.startsWith(UTF8_BOM)).toBe(true);
        const bytes = new TextEncoder().encode(csv);
        expect([bytes[0], bytes[1], bytes[2]]).toEqual([0xef, 0xbb, 0xbf]);
    });

    it('omits the BOM by default', () => {
        const csv = buildCsv(['a'], [['1']]);
        const bytes = new TextEncoder().encode(csv);
        expect(bytes[0]).not.toBe(0xef);
    });
});

describe('buildJson', () => {
    it('round-trips a bigint PK without precision loss (exact, not Number-coerced)', () => {
        const json = buildJson(['id', 'name'], [[9007199254740993n, 'row']]);
        const parsed = JSON.parse(json) as { id: unknown; name: string }[];
        // bigint -> exact decimal string, never coerced through Number (which would
        // round 9007199254740993 -> 9007199254740992).
        expect(parsed[0].id).toBe('9007199254740993');
        expect(Number(parsed[0].id)).not.toBe(9007199254740993);
        expect(parsed[0].name).toBe('row');
    });

    it('preserves a numeric-string PK verbatim', () => {
        const json = buildJson(['id'], [['9007199254740993']]);
        expect(JSON.parse(json)[0].id).toBe('9007199254740993');
    });

    it('serializes dates in a stable ISO-8601 format', () => {
        const json = buildJson(['t'], [[new Date('2026-07-14T00:00:00.000Z')]]);
        expect(JSON.parse(json)[0].t).toBe('2026-07-14T00:00:00.000Z');
    });

    it('preserves NULL as JSON null (distinct from empty string)', () => {
        const json = buildJson(['a', 'b'], [[null, '']]);
        const parsed = JSON.parse(json)[0];
        expect(parsed.a).toBeNull();
        expect(parsed.b).toBe('');
    });

    it('emits one object per line in lines mode', () => {
        const json = buildJson(['a'], [[1], [2]], { mode: 'lines' });
        const lines = json.split('\n');
        expect(lines).toHaveLength(2);
        expect(JSON.parse(lines[0]).a).toBe(1);
        expect(JSON.parse(lines[1]).a).toBe(2);
    });
});

describe('exportAllRows', () => {
    // A fixture of `count` rows the fake fetcher pages through — stands in for the
    // server's filtered+sorted result set. Asserting rowCount === total proves the
    // export drained every matching row across pages, not just the visible page.
    function makeFetcher(count: number, pageSize: number) {
        const all = Array.from({ length: count }, (_, i) => [i, `name${i}`]);
        const fetchPage = vi.fn(
            async (offset: number, limit: number): Promise<ExportPage> => ({
                rows: all.slice(offset, offset + limit),
                total: count,
            }),
        );
        return { fetchPage, pageSize };
    }

    it('pages through every matching row across multiple pages (count === total)', async () => {
        const { fetchPage } = makeFetcher(250, 100);
        const result = await exportAllRows({
            headers: ['id', 'name'],
            format: 'csv',
            fetchPage,
            pageSize: 100,
        });
        expect(result.total).toBe(250);
        expect(result.rowCount).toBe(250);
        expect(result.truncated).toBe(false);
        // Proves it exceeded one page: 250/100 -> 3 fetches.
        expect(fetchPage.mock.calls.length).toBeGreaterThan(1);
        // The CSV carries all 250 data rows + 1 header row.
        expect(result.content.split('\r\n')).toHaveLength(251);
    });

    it('reports progress as pages arrive', async () => {
        const { fetchPage } = makeFetcher(250, 100);
        const progress: number[] = [];
        await exportAllRows({
            headers: ['id', 'name'],
            format: 'csv',
            fetchPage,
            pageSize: 100,
            onProgress: (fetched) => progress.push(fetched),
        });
        expect(progress[progress.length - 1]).toBe(250);
    });

    it('caps at rowCap and marks the result truncated', async () => {
        const { fetchPage } = makeFetcher(250, 100);
        const result = await exportAllRows({
            headers: ['id', 'name'],
            format: 'json',
            fetchPage,
            pageSize: 100,
            rowCap: 100,
        });
        expect(result.rowCount).toBe(100);
        expect(result.truncated).toBe(true);
    });

    it('rejects when the signal is already aborted, producing no content', async () => {
        const { fetchPage } = makeFetcher(250, 100);
        const controller = new AbortController();
        controller.abort();
        await expect(
            exportAllRows({
                headers: ['id', 'name'],
                format: 'csv',
                fetchPage,
                pageSize: 100,
                signal: controller.signal,
            }),
        ).rejects.toThrow();
        // Cancel before any page: the fetcher was never even called, so no partial
        // result could have been assembled or written.
        expect(fetchPage).not.toHaveBeenCalled();
    });

    it('stops paging when cancelled mid-export (no partial file is produced)', async () => {
        const controller = new AbortController();
        const all = Array.from({ length: 300 }, (_, i) => [i]);
        const fetchPage = vi.fn(
            async (offset: number, limit: number): Promise<ExportPage> => {
                // Abort after the first page is served; the loop must not assemble
                // and return a partial content string — it must throw so the caller
                // downloads nothing.
                if (offset >= 100) controller.abort();
                return { rows: all.slice(offset, offset + limit), total: 300 };
            },
        );
        await expect(
            exportAllRows({
                headers: ['id'],
                format: 'csv',
                fetchPage,
                pageSize: 100,
                signal: controller.signal,
            }),
        ).rejects.toThrow();
    });
});
