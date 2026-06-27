import { describe, it, expect } from 'vitest';
import { validateFieldValue, anchorPattern } from './field-validation';
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
