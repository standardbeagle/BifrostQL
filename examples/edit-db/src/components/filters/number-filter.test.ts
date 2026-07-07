import { describe, expect, it } from 'vitest';
import { parseNumeric } from './number-filter';

describe('parseNumeric', () => {
    it('parses exact integer and float values', () => {
        expect(parseNumeric('42', true)).toBe(42);
        expect(parseNumeric('-7', true)).toBe(-7);
        expect(parseNumeric('3.14', false)).toBe(3.14);
        expect(parseNumeric('.5', false)).toBe(0.5);
        expect(parseNumeric('1e3', false)).toBe(1000);
    });

    it('rejects partial numeric strings instead of truncating them', () => {
        expect(parseNumeric('12abc', true)).toBeNull();
        expect(parseNumeric('12.5', true)).toBeNull();
        expect(parseNumeric('1.2.3', false)).toBeNull();
        expect(parseNumeric('Infinity', false)).toBeNull();
    });

    it('treats empty sign-only input as unset', () => {
        expect(parseNumeric('', true)).toBeNull();
        expect(parseNumeric('-', true)).toBeNull();
        expect(parseNumeric('+', false)).toBeNull();
    });
});
