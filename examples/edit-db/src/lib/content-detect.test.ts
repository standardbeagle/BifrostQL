import { describe, expect, it } from 'vitest';
import { detectContentKind, isJsonDbType } from './content-detect';

describe('isJsonDbType', () => {
    it('recognizes native JSON dbTypes case-insensitively', () => {
        expect(isJsonDbType('json')).toBe(true);
        expect(isJsonDbType('JSON')).toBe(true);
        expect(isJsonDbType('jsonb')).toBe(true);
    });

    it('rejects non-JSON dbTypes', () => {
        expect(isJsonDbType('nvarchar')).toBe(false);
        expect(isJsonDbType('text')).toBe(false);
    });
});

describe('detectContentKind JSON handling', () => {
    it('detects JSON by value heuristic', () => {
        expect(detectContentKind('{"a":1}')).toBe('json');
        expect(detectContentKind('[1,2,3]')).toBe('json');
    });

    it('trusts a native JSON dbType even when the value is not bracketed', () => {
        // A JSON scalar value (top-level string/number) still declared as json.
        expect(detectContentKind('42', 'jsonb')).toBe('json');
        expect(detectContentKind('"hello"', 'json')).toBe('json');
    });

    it('does not treat plain text in a non-JSON column as JSON', () => {
        expect(detectContentKind('hello world', 'nvarchar')).toBe('text');
    });
});
