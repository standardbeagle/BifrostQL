import { describe, it, expect } from 'vitest';
import { matchesLabel } from './label-match';

describe('matchesLabel', () => {
    it('matches case-insensitively against the label', () => {
        expect(matchesLabel({ key: 1, label: 'Algebra' }, 'key', 'alge')).toBe(true);
        expect(matchesLabel({ key: 1, label: 'Algebra' }, 'key', 'ALGE')).toBe(true);
        expect(matchesLabel({ key: 1, label: 'Algebra' }, 'key', 'biology')).toBe(false);
    });

    it('falls back to the id field when the label is missing', () => {
        expect(matchesLabel({ id: 1042 }, 'id', '104')).toBe(true);
        expect(matchesLabel({ id: 1042 }, 'id', '999')).toBe(false);
    });

    it('treats a row with neither label nor id as an empty string', () => {
        expect(matchesLabel({}, 'id', 'x')).toBe(false);
        expect(matchesLabel({}, 'id', '')).toBe(true); // empty term matches everything
    });

    it('coerces non-string labels', () => {
        expect(matchesLabel({ key: 'a', label: 42 }, 'key', '4')).toBe(true);
    });
});
