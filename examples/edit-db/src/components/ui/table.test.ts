import { describe, it, expect } from 'vitest';
import { TABLE_ROW_HEIGHT, TABLE_HEADER_HEIGHT } from './table';

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
