import { describe, expect, it } from 'vitest';
import { buildFkLookupQuery, buildFkTupleLookupQuery, fkTupleFor } from './fk-filter';

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

describe('buildFkTupleLookupQuery', () => {
    const parentColumns = [{ name: 'tenant_id' }, { name: 'order_id' }, { name: 'title' }];

    it('projects the label and every destination column under positional aliases', () => {
        expect(buildFkTupleLookupQuery('orders', 'title', ['tenant_id', 'order_id'], parentColumns))
            .toBe('query LookupTupleorders { orders(sort: [title_asc], limit: 100) { data { label: title k0: tenant_id k1: order_id } } }');
    });

    it('refuses names that are not GraphQL names or not on the parent table', () => {
        expect(buildFkTupleLookupQuery('orders', 'title', ['tenant_id', 'order_id) { x'], parentColumns)).toBeNull();
        expect(buildFkTupleLookupQuery('orders', 'title', ['tenant_id', 'missing'], parentColumns)).toBeNull();
        expect(buildFkTupleLookupQuery('orders', 'title', [], parentColumns)).toBeNull();
    });
});

describe('fkTupleFor', () => {
    it('pairs each source column with the parent key part in FK order, values untouched', () => {
        expect(fkTupleFor({ label: 'A', k0: 7, k1: '9007199254740993' }, ['tenant_id', 'order_id']))
            .toEqual([['tenant_id', 7], ['order_id', '9007199254740993']]);
    });

    it('yields no tuple for a parent with a NULL or missing key part', () => {
        expect(fkTupleFor({ label: 'A', k0: 7, k1: null }, ['tenant_id', 'order_id'])).toBeNull();
        expect(fkTupleFor({ label: 'A', k0: 7 }, ['tenant_id', 'order_id'])).toBeNull();
    });
});
