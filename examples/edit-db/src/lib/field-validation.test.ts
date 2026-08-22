import { describe, it, expect } from 'vitest';
import { validateFieldValue, validateRowValues, anchorPattern } from './field-validation';
import type { Column } from '../types/schema';

function col(overrides: Partial<Column>): Column {
    return {
        name: 'f',
        label: 'Field',
        paramType: 'String',
        isNullable: true,
        ...overrides,
    } as Column;
}

describe('anchorPattern', () => {
    it('wraps an unanchored pattern as ^(?:...)$', () => {
        expect(anchorPattern('[a-z]+')).toBe('^(?:[a-z]+)$');
    });

    it('leaves an already-anchored pattern untouched', () => {
        expect(anchorPattern('^[a-z]+$')).toBe('^[a-z]+$');
    });
});

describe('validateFieldValue — pattern parity', () => {
    it('rejects a substring match the server would reject (anchored)', () => {
        // Unanchored "[0-9]{3}" would match "abc123def" as a substring; anchoring
        // makes it fail, matching the server.
        const c = col({ pattern: '[0-9]{3}' });
        expect(validateFieldValue(c, 'abc123def', false)).toBeTruthy();
        expect(validateFieldValue(c, '123', false)).toBeUndefined();
    });

    it('uses patternMessage when provided', () => {
        const c = col({ pattern: '[0-9]+', patternMessage: 'digits only' });
        expect(validateFieldValue(c, 'x', false)).toBe('digits only');
    });

    it('reports an invalid pattern rather than throwing', () => {
        const c = col({ pattern: '(' });
        expect(validateFieldValue(c, 'x', false)).toContain('invalid validation pattern');
    });
});

describe('validateFieldValue — required + length', () => {
    it('flags a missing required value', () => {
        expect(validateFieldValue(col({}), '', true)).toBe('Field is required');
    });

    it('skips remaining checks for empty optional values', () => {
        expect(validateFieldValue(col({ minLength: 5 }), '', false)).toBeUndefined();
    });

    it('enforces min/max length', () => {
        expect(validateFieldValue(col({ minLength: 3 }), 'ab', false)).toContain('at least 3');
        expect(validateFieldValue(col({ maxLength: 2 }), 'abc', false)).toContain('at most 2');
    });
});

describe('validateFieldValue — email/url', () => {
    it('validates email inputType', () => {
        const c = col({ inputType: 'email' });
        expect(validateFieldValue(c, 'a@b.com', false)).toBeUndefined();
        expect(validateFieldValue(c, 'not-an-email', false)).toBe('Invalid email address');
        expect(validateFieldValue(c, 'a@b', false)).toBe('Invalid email address');
        expect(validateFieldValue(c, 'a b@c.com', false)).toBe('Invalid email address');
    });

    it('validates url inputType (http/https only)', () => {
        const c = col({ inputType: 'url' });
        expect(validateFieldValue(c, 'https://example.com', false)).toBeUndefined();
        expect(validateFieldValue(c, 'ftp://example.com', false)).toBe('Invalid URL');
        expect(validateFieldValue(c, 'nope', false)).toBe('Invalid URL');
    });
});

describe('validateFieldValue — numeric bounds', () => {
    it('enforces min/max on numeric columns', () => {
        const c = col({ paramType: 'Int', min: 1, max: 10 });
        expect(validateFieldValue(c, '0', false)).toContain('at least 1');
        expect(validateFieldValue(c, '11', false)).toContain('at most 10');
        expect(validateFieldValue(c, '5', false)).toBeUndefined();
    });
});

describe('validateFieldValue — numeric well-formedness', () => {
    // Without these the form accepted the value, coerceNumericValue then threw
    // during Save, and that throw happened BEFORE mutateAsync so nothing was
    // rendered: Save appeared to do nothing at all.
    it('rejects a non-integer value in an Int column', () => {
        const c = col({ paramType: 'Int', label: 'Count' });
        expect(validateFieldValue(c, '1.5', false)).toBe('Count must be a whole number');
        expect(validateFieldValue(c, 1.5, false)).toBe('Count must be a whole number');
        expect(validateFieldValue(c, '2', false)).toBeUndefined();
        expect(validateFieldValue(c, '-2', false)).toBeUndefined();
        expect(validateFieldValue(c, '2.0', false)).toBeUndefined();
    });

    it('rejects text that is not a number at all in a numeric column', () => {
        const c = col({ paramType: 'Float', label: 'Score' });
        expect(validateFieldValue(c, 'abc', false)).toBe('Score must be a number');
        expect(validateFieldValue(c, '1.25', false)).toBeUndefined();
    });

    it('allows a Float column to hold a fractional value', () => {
        const c = col({ paramType: 'Float', label: 'Score' });
        expect(validateFieldValue(c, '1.5', false)).toBeUndefined();
    });

    it('still enforces min/max after the well-formedness check', () => {
        const c = col({ paramType: 'Int', min: 1, max: 10, label: 'Count' });
        expect(validateFieldValue(c, '0', false)).toContain('at least 1');
    });
});

describe('validateFieldValue — schema-derived checks', () => {
    it('refuses an unparseable datetime string on a temporal column', () => {
        const c = col({ dbType: 'datetime', paramType: 'String' });
        expect(validateFieldValue(c, 'not-a-date', false)).toContain('valid date/time');
        expect(validateFieldValue(c, '2024-06-01T12:30:00', false)).toBeUndefined();
    });

    it('refuses an unparseable bare date and time', () => {
        expect(validateFieldValue(col({ dbType: 'date' }), 'yesterday-ish', false)).toContain('valid date');
        expect(validateFieldValue(col({ dbType: 'date' }), '2024-06-01', false)).toBeUndefined();
        expect(validateFieldValue(col({ dbType: 'time' }), '25:99:99x', false)).toContain('valid time');
        expect(validateFieldValue(col({ dbType: 'time' }), '12:30:00', false)).toBeUndefined();
    });

    it('bounds Int/Short/Byte to their scalar ranges', () => {
        expect(validateFieldValue(col({ paramType: 'Int', dbType: 'int' }), 3000000000, false)).toContain('between');
        expect(validateFieldValue(col({ paramType: 'Short', dbType: 'smallint' }), 40000, false)).toContain('between');
        expect(validateFieldValue(col({ paramType: 'Byte', dbType: 'tinyint' }), -1, false)).toContain('between');
        expect(validateFieldValue(col({ paramType: 'Int', dbType: 'int' }), 42, false)).toBeUndefined();
    });

    it('bounds BigInt as text without losing precision', () => {
        const c = col({ paramType: 'BigInt', dbType: 'bigint' });
        expect(validateFieldValue(c, '9223372036854775808', false)).toContain('64-bit');
        expect(validateFieldValue(c, '9223372036854775807', false)).toBeUndefined();
        expect(validateFieldValue(c, '12.5', false)).toContain('whole number');
    });

    it('refuses a decimal integer-part overflow but allows fractional rounding', () => {
        const c = col({ paramType: 'Decimal', dbType: 'decimal', precision: 5, scale: 2 });
        expect(validateFieldValue(c, '1234.5', false)).toContain('3 digits');
        expect(validateFieldValue(c, '1.239', false)).toBeUndefined();
    });
});

describe('validateRowValues', () => {
    it('collects every failing present value and skips absent ones', () => {
        const columns = [
            col({ name: 'qty', label: 'Qty', paramType: 'Int', dbType: 'int' }),
            col({ name: 'when', label: 'When', paramType: 'String', dbType: 'datetime' }),
            col({ name: 'note', label: 'Note', maxLength: 5 }),
        ];
        const errors = validateRowValues(columns, {
            qty: 3000000000,
            when: 'not-a-date',
            // note absent — must not produce a required error (form policy, not ours)
        });
        expect(errors).toHaveLength(2);
        expect(errors.join(' ')).toContain('Qty');
        expect(errors.join(' ')).toContain('When');
    });

    it('returns empty for a clean payload', () => {
        const columns = [col({ name: 'qty', label: 'Qty', paramType: 'Int', dbType: 'int' })];
        expect(validateRowValues(columns, { qty: 7 })).toEqual([]);
    });
});
