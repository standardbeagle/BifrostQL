import { describe, it, expect } from 'vitest';
import { reformatJsonText } from './json-text';

describe('reformatJsonText', () => {
    it('keeps an integer past 2^53 exact', () => {
        const out = reformatJsonText('{"orderId": 9007199254740993, "note": "x"}', 2);
        expect(out).toBe('{\n  "orderId": 9007199254740993,\n  "note": "x"\n}');
    });

    it('keeps number spellings a double round-trip rewrites', () => {
        expect(reformatJsonText('[1.50,1e3,-0,0.1000000000000000055511151231257827]', 0))
            .toBe('[1.50,1e3,-0,0.1000000000000000055511151231257827]');
    });

    it('matches JSON.stringify layout for values a double holds exactly', () => {
        const value = { a: [1, 2, { b: null, c: true }], d: {}, e: [], f: 'x' };
        const compact = JSON.stringify(value);
        expect(reformatJsonText(compact, 2)).toBe(JSON.stringify(value, null, 2));
        expect(reformatJsonText(JSON.stringify(value, null, 4), 0)).toBe(compact);
    });

    it('copies strings verbatim, including escapes and structural characters', () => {
        const text = '{"k": "a\\"b, c: [d] {e}\\\\", "u": "\\u00e9"}';
        expect(reformatJsonText(text, 0)).toBe('{"k":"a\\"b, c: [d] {e}\\\\","u":"\\u00e9"}');
    });

    it('returns null for text that is not JSON', () => {
        expect(reformatJsonText('{"a": }', 2)).toBeNull();
        expect(reformatJsonText('', 2)).toBeNull();
    });

    it('formats a top-level scalar as itself', () => {
        expect(reformatJsonText('  9007199254740993 ', 2)).toBe('9007199254740993');
    });
});
