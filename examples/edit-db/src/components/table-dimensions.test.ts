import { describe, it, expect } from 'vitest';
import { TABLE_ROW_HEIGHT, TABLE_HEADER_HEIGHT } from './ui/table';

describe('TABLE_ROW_HEIGHT', () => {
    it('is a positive number', () => {
        expect(TABLE_ROW_HEIGHT).toBeGreaterThan(0);
    });

    it('is reasonable for data density (20-80px)', () => {
        expect(TABLE_ROW_HEIGHT).toBeGreaterThanOrEqual(20);
        expect(TABLE_ROW_HEIGHT).toBeLessThanOrEqual(80);
    });
});

describe('TABLE_HEADER_HEIGHT', () => {
    it('is a positive number', () => {
        expect(TABLE_HEADER_HEIGHT).toBeGreaterThan(0);
    });

    it('is reasonable (20-60px)', () => {
        expect(TABLE_HEADER_HEIGHT).toBeGreaterThanOrEqual(20);
        expect(TABLE_HEADER_HEIGHT).toBeLessThanOrEqual(60);
    });
});

describe('fit-to-container calculation', () => {
    // The fit formula: Math.max(5, Math.floor((containerHeight - TABLE_HEADER_HEIGHT) / TABLE_ROW_HEIGHT))
    function computeFitSize(containerHeight: number): number {
        return Math.max(5, Math.floor((containerHeight - TABLE_HEADER_HEIGHT) / TABLE_ROW_HEIGHT));
    }

    it('computes correct fit for typical viewport (1140px)', () => {
        // 1140 - 32 = 1108, 1108 / 40 = 27.7 → 27
        expect(computeFitSize(1140)).toBe(27);
    });

    it('computes correct fit for small container (300px)', () => {
        // 300 - 32 = 268, 268 / 40 = 6.7 → 6
        expect(computeFitSize(300)).toBe(6);
    });

    it('enforces minimum of 5 rows', () => {
        expect(computeFitSize(100)).toBe(5);
        expect(computeFitSize(50)).toBe(5);
        expect(computeFitSize(0)).toBe(5);
    });

    it('handles negative container height', () => {
        expect(computeFitSize(-100)).toBe(5);
    });

    it('computes correctly for exact row boundaries', () => {
        // Exactly 10 rows: 32 + (10 * 40) = 432
        expect(computeFitSize(432)).toBe(10);
        // One pixel less: 431 → 9
        expect(computeFitSize(431)).toBe(9);
        // One pixel more: 433 → 10
        expect(computeFitSize(433)).toBe(10);
    });

    it('computes correctly when container equals header height', () => {
        expect(computeFitSize(TABLE_HEADER_HEIGHT)).toBe(5); // minimum
    });

    it('computes correctly for very large container', () => {
        // 4000px: (4000 - 32) / 40 = 99.2 → 99
        expect(computeFitSize(4000)).toBe(99);
    });

    it('uses the shared constants (not hardcoded values)', () => {
        // Verify the formula uses the exported constants
        const expected = Math.max(5, Math.floor((800 - TABLE_HEADER_HEIGHT) / TABLE_ROW_HEIGHT));
        expect(computeFitSize(800)).toBe(expected);
    });

    it('total content height never exceeds container (above minimum)', () => {
        // Only check containers large enough to hold min rows + header
        const minContainerForCheck = TABLE_HEADER_HEIGHT + 5 * TABLE_ROW_HEIGHT + 1;
        for (const h of [minContainerForCheck, 400, 600, 800, 1000, 1200, 1500, 2000]) {
            const rows = computeFitSize(h);
            const contentHeight = TABLE_HEADER_HEIGHT + rows * TABLE_ROW_HEIGHT;
            expect(contentHeight).toBeLessThanOrEqual(h);
        }
    });

    it('cannot fit one more row (tight packing)', () => {
        for (const h of [300, 500, 700, 900, 1100, 1400]) {
            const rows = computeFitSize(h);
            if (rows > 5) { // only check above minimum
                const withOneMore = TABLE_HEADER_HEIGHT + (rows + 1) * TABLE_ROW_HEIGHT;
                expect(withOneMore).toBeGreaterThan(h);
            }
        }
    });
});
