import { describe, expect, it } from 'vitest';
import { buildFkLookupQuery } from './fk-filter';

const columns = [{ name: 'id' }, { name: 'name' }, { name: 'code' }];

describe('buildFkLookupQuery', () => {
    it('builds a sorted lookup query for valid schema names', () => {
        expect(buildFkLookupQuery('users', 'name', 'id', columns)).toBe(
            'query Lookupusers { users(sort: [name_asc], limit: 100) { data { id: id label: name } } }',
        );
    });

    it('returns null for unsafe GraphQL names', () => {
        expect(buildFkLookupQuery('users) { injected', 'name', 'id', columns)).toBeNull();
        expect(buildFkLookupQuery('users', 'name) { injected', 'id', columns)).toBeNull();
        expect(buildFkLookupQuery('users', 'name', 'id) { injected', columns)).toBeNull();
    });

    it('returns null when projected columns are absent from the joined table schema', () => {
        expect(buildFkLookupQuery('users', 'missing', 'id', columns)).toBeNull();
        expect(buildFkLookupQuery('users', 'name', 'missing', columns)).toBeNull();
    });
});
