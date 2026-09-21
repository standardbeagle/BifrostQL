import { describe, expect, it } from 'vitest';
import { pickDefaultSearchColumn } from './search-column';

describe('pickDefaultSearchColumn', () => {
    const options = [
        { key: 'id', value: 'id,Int!' },
        { key: 'number', value: 'number,String' },
        { key: 'name', value: 'name,String' },
    ];

    it('starts on the configured column when it is searchable', () => {
        // Users who navigate by a business key (workshop number) should not
        // have to switch the picker off `id` on every visit.
        expect(pickDefaultSearchColumn(options, 'number')).toBe('number,String');
    });

    it('falls back to the first option when nothing is configured', () => {
        expect(pickDefaultSearchColumn(options, undefined)).toBe('id,Int!');
    });

    it('falls back to the first option when the configured column is not searchable', () => {
        // A DateTime/Boolean column, or a name the table lacks, must not leave
        // the picker on a value the filter builder cannot act on.
        expect(pickDefaultSearchColumn(options, 'createdOn')).toBe('id,Int!');
    });

    it('is empty when the table has no searchable columns', () => {
        expect(pickDefaultSearchColumn([], 'number')).toBe('');
        expect(pickDefaultSearchColumn(undefined, 'number')).toBe('');
    });
});
