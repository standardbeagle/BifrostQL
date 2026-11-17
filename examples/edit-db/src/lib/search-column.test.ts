import { describe, expect, it } from 'vitest';
import { pickDefaultSearchColumn, quickSearchUrl } from './search-column';

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

describe('quickSearchUrl', () => {
    const current = new URLSearchParams({ cf: '[["status","_eq","open"]]', gb: 'status', gs: 'amount' });

    it('keeps column filters and grouping, replacing only the header filter', () => {
        const url = quickSearchUrl(current, 'name', 'String', 'ann')!;
        const params = new URLSearchParams(url.slice(1));
        expect(params.get('cf')).toBe('[["status","_eq","open"]]');
        expect(params.get('gb')).toBe('status');
        expect(params.get('gs')).toBe('amount');
        expect(JSON.parse(params.get('filter')!)).toEqual(['name', '_contains', 'ann', 'String']);
    });

    it('carries URL-reserved characters in the search term intact', () => {
        for (const term of ['R&D', 'a+b', 'x#y', '50%', 'a=b']) {
            const url = quickSearchUrl(new URLSearchParams(), 'name', 'String', term)!;
            expect(JSON.parse(new URLSearchParams(url.slice(1)).get('filter')!)[2]).toBe(term);
            expect(url).not.toContain('#');
        }
    });

    it('keeps a bigint term as its digits and refuses non-numeric input on a numeric column', () => {
        const url = quickSearchUrl(new URLSearchParams(), 'id', 'BigInt!', '9007199254740993')!;
        expect(JSON.parse(new URLSearchParams(url.slice(1)).get('filter')!)).toEqual(['id', '_eq', '9007199254740993', 'BigInt!']);
        expect(quickSearchUrl(new URLSearchParams(), 'id', 'Int!', 'abc')).toBeNull();
    });
});
