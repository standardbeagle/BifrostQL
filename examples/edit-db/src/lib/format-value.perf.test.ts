import { describe, it } from 'vitest';
import { formatColumnValue, formatDateCellValue } from './format-value';
import type { Column } from '../types/schema';

/**
 * Per-cell cost of the grid's type-based rendering: one 1,000-cell page per column kind.
 * Opt-in timing harness, skipped in the normal suite:
 *   EDITDB_PERF=1 pnpm --dir examples/edit-db exec vitest run src/lib/format-value.perf.test.ts
 * Prints the median of 25 timed passes after 5 warm-up passes.
 */

function col(partial: Partial<Column>): Column {
    return { name: 'c', paramType: '', dbType: '', metadata: {}, ...partial } as Column;
}

const ROWS = 1000;
const dates = Array.from({ length: ROWS }, (_, i) => new Date(Date.UTC(2026, 0, 1) + i * 3_600_000).toISOString());
const ints = Array.from({ length: ROWS }, (_, i) => i * 1237);
const codes = Array.from({ length: ROWS }, (_, i) => i % 4);
const strings = Array.from({ length: ROWS }, (_, i) => `name ${i}`);

const datetimeColumn = col({ paramType: 'DateTimeOffset', dbType: 'datetimeoffset' });
const numberColumn = col({ paramType: 'Int', dbType: 'int', metadata: { 'display-format': 'number' } });
const percentColumn = col({ paramType: 'Float', dbType: 'float', metadata: { 'display-format': 'percent' } });
const enumColumn = col({
    paramType: 'Int', dbType: 'int',
    enumValues: ['0', '1', '2', '3'], enumLabels: ['Draft', 'Active', 'Archived', 'Deleted'],
} as Partial<Column>);
const stringColumn = col({ paramType: 'String', dbType: 'nvarchar' });

const cases: [string, () => void][] = [
    ['DateTime grid cell (formatDateCellValue)', () => { for (const v of dates) formatDateCellValue(v); }],
    ['datetime column', () => { for (const v of dates) formatColumnValue(v, datetimeColumn); }],
    ['number column', () => { for (const v of ints) formatColumnValue(v, numberColumn); }],
    ['percent column', () => { for (const v of ints) formatColumnValue(v / 100000, percentColumn); }],
    ['labelled enum column', () => { for (const v of codes) formatColumnValue(v, enumColumn); }],
    ['plain string column', () => { for (const v of strings) formatColumnValue(v, stringColumn); }],
];

function medianMs(run: () => void): number {
    for (let i = 0; i < 5; i++) run();
    const times: number[] = [];
    for (let i = 0; i < 25; i++) {
        const start = performance.now();
        run();
        times.push(performance.now() - start);
    }
    times.sort((a, b) => a - b);
    return times[Math.floor(times.length / 2)];
}

describe.skipIf(!process.env.EDITDB_PERF)(`format cost, ${ROWS} cells`, () => {
    it('reports per-column-kind timings', () => {
        const rows = cases.map(([name, run]) => {
            const ms = medianMs(run);
            return `| ${name} | ${ms.toFixed(3)} ms | ${((ms * 1000) / ROWS).toFixed(2)} µs |`;
        });
        console.log(['| column kind | 1,000 cells | per cell |', '|---|---:|---:|', ...rows].join('\n'));
    });
});
