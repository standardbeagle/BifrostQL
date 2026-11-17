import { describe, expect, it } from 'vitest';
import { isUnchangedCellValue } from './cell-commit';

describe('isUnchangedCellValue', () => {
    it('treats the blank the editor seeded for a NULL cell as unchanged', () => {
        expect(isUnchangedCellValue(null, '')).toBe(true);
        expect(isUnchangedCellValue(undefined, '')).toBe(true);
        expect(isUnchangedCellValue(null, null)).toBe(true);
    });

    it('still stages a real value typed into a NULL cell', () => {
        expect(isUnchangedCellValue(null, 'x')).toBe(false);
        expect(isUnchangedCellValue(null, 0)).toBe(false);
    });

    it('stages clearing a cell that held a value, and compares values as text', () => {
        expect(isUnchangedCellValue('abc', '')).toBe(false);
        expect(isUnchangedCellValue('abc', null)).toBe(false);
        expect(isUnchangedCellValue(5, '5')).toBe(true);
        expect(isUnchangedCellValue('', '')).toBe(true);
    });
});
