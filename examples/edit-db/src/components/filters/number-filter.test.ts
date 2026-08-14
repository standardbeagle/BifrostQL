import { describe, expect, it } from 'vitest';
import { parseNumeric } from './number-filter';

describe('parseNumeric', () => {
    it('parses exact integer and float values', () => {
        expect(parseNumeric('42', 'Int')).toBe(42);
        expect(parseNumeric('-7', 'Int')).toBe(-7);
        expect(parseNumeric('3.14', 'Float')).toBe(3.14);
        expect(parseNumeric('.5', 'Float')).toBe(0.5);
        expect(parseNumeric('1e3', 'Float')).toBe(1000);
    });

    it('rejects partial numeric strings instead of truncating them', () => {
        expect(parseNumeric('12abc', 'Int')).toBeNull();
        expect(parseNumeric('12.5', 'Int')).toBeNull();
        expect(parseNumeric('1.2.3', 'Float')).toBeNull();
        expect(parseNumeric('Infinity', 'Float')).toBeNull();
    });

    // smallint/tinyint are whole numbers like Int — they used to reach no filter
    // control at all, so nothing here classified them.
    it('treats Short and Byte as whole-number columns', () => {
        expect(parseNumeric('6', 'Short')).toBe(6);
        expect(parseNumeric('1', 'Byte!')).toBe(1);
        expect(parseNumeric('6.5', 'Short')).toBeNull();
    });

    // A BigInt/Decimal bound must reach the wire as the digits the user typed.
    // Through a JS number, 9007199254740993 becomes ...992 — the filter then
    // silently matches a different row than the one asked for.
    it('keeps a bigint or decimal value as the typed digits, not a number', () => {
        expect(parseNumeric('9007199254740993', 'BigInt')).toBe('9007199254740993');
        expect(parseNumeric('12345678901234567.89', 'Decimal')).toBe('12345678901234567.89');
        expect(parseNumeric(' 42 ', 'BigInt!')).toBe('42');
    });

    it('still validates a bigint or decimal value before accepting it', () => {
        expect(parseNumeric('12abc', 'BigInt')).toBeNull();
        expect(parseNumeric('12.5', 'BigInt')).toBeNull();
        expect(parseNumeric('', 'Decimal')).toBeNull();
    });

    it('treats empty sign-only input as unset', () => {
        expect(parseNumeric('', 'Int')).toBeNull();
        expect(parseNumeric('-', 'Int')).toBeNull();
        expect(parseNumeric('+', 'Float')).toBeNull();
    });
});
