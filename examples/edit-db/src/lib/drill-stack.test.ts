import { describe, it, expect } from 'vitest';
import {
    framesEqual,
    pushDrillFrame,
    popDrillFramesTo,
    buildDrillCrumbs,
    type DrillFrame,
} from './drill-stack';

const frame = (overrides: Partial<DrillFrame> = {}): DrillFrame => ({
    tableName: 'orders',
    filterTable: 'customers',
    filterId: '1',
    filterColumn: 'customer_id',
    ...overrides,
});

describe('framesEqual', () => {
    it('returns true for identical frames', () => {
        expect(framesEqual(frame(), frame())).toBe(true);
    });

    it('returns false when tableName differs', () => {
        expect(framesEqual(frame(), frame({ tableName: 'invoices' }))).toBe(false);
    });

    it('returns false when filterId differs', () => {
        expect(framesEqual(frame(), frame({ filterId: '2' }))).toBe(false);
    });

    it('returns false when filterColumn differs', () => {
        expect(framesEqual(frame(), frame({ filterColumn: 'other_id' }))).toBe(false);
    });

    it('treats undefined and missing filter fields the same', () => {
        const a: DrillFrame = { tableName: 'orders' };
        const b: DrillFrame = { tableName: 'orders', filterId: undefined };
        expect(framesEqual(a, b)).toBe(true);
    });
});

describe('pushDrillFrame', () => {
    it('appends to an empty stack', () => {
        const next = pushDrillFrame([], frame());
        expect(next).toEqual([frame()]);
    });

    it('appends a new distinct frame', () => {
        const f1 = frame();
        const f2 = frame({ tableName: 'lineItems', filterTable: 'orders', filterId: '5', filterColumn: 'order_id' });
        const next = pushDrillFrame([f1], f2);
        expect(next).toEqual([f1, f2]);
    });

    it('is a no-op when the top frame matches', () => {
        const f1 = frame();
        const stack = [f1];
        const next = pushDrillFrame(stack, frame());
        expect(next).toBe(stack);
    });

    it('truncates back to an existing ancestor frame (cycle guard)', () => {
        const customers = frame({ tableName: 'customers', filterTable: undefined, filterId: undefined, filterColumn: undefined });
        const orders = frame();
        const lineItems = frame({ tableName: 'lineItems', filterTable: 'orders', filterId: '5', filterColumn: 'order_id' });

        const stack = [customers, orders, lineItems];
        // User clicks a link back to `customers` (already in stack).
        const next = pushDrillFrame(stack, customers);
        expect(next).toEqual([customers]);
    });

    it('handles self-referential drill by appending a different filterId', () => {
        // categories/id=1 (parent) -> drilling child categories rows whose parent_id=1
        const level1 = frame({ tableName: 'categories', filterTable: 'categories', filterId: '1', filterColumn: 'parent_id' });
        const level2 = frame({ tableName: 'categories', filterTable: 'categories', filterId: '5', filterColumn: 'parent_id' });
        const next = pushDrillFrame([level1], level2);
        expect(next).toEqual([level1, level2]);
    });

    it('does not mutate the input array', () => {
        const stack: DrillFrame[] = [];
        pushDrillFrame(stack, frame());
        expect(stack).toEqual([]);
    });
});

describe('popDrillFramesTo', () => {
    const a = frame({ tableName: 'a' });
    const b = frame({ tableName: 'b' });
    const c = frame({ tableName: 'c' });

    it('returns slice up to index (exclusive)', () => {
        expect(popDrillFramesTo([a, b, c], 2)).toEqual([a, b]);
        expect(popDrillFramesTo([a, b, c], 1)).toEqual([a]);
    });

    it('empties the stack when index is 0', () => {
        expect(popDrillFramesTo([a, b, c], 0)).toEqual([]);
    });

    it('empties the stack for negative index', () => {
        expect(popDrillFramesTo([a, b, c], -1)).toEqual([]);
    });

    it('is a no-op when index >= length', () => {
        const stack = [a, b];
        const next = popDrillFramesTo(stack, 5);
        expect(next).toBe(stack);
    });
});

describe('buildDrillCrumbs', () => {
    const labels: Record<string, string> = {
        customers: 'Customers',
        orders: 'Orders',
        lineItems: 'Line Items',
        categories: 'Categories',
    };
    const lookup = (name: string) => labels[name];

    it('returns only the root crumb for an empty stack', () => {
        const crumbs = buildDrillCrumbs('customers', [], lookup);
        expect(crumbs).toEqual([{ index: -1, label: 'Customers' }]);
    });

    it('builds a breadcrumb chain for a multi-level drill', () => {
        const stack: DrillFrame[] = [
            frame({ tableName: 'orders', filterTable: 'customers', filterId: '42', filterColumn: 'customer_id' }),
            frame({ tableName: 'lineItems', filterTable: 'orders', filterId: '7', filterColumn: 'order_id' }),
        ];
        const crumbs = buildDrillCrumbs('customers', stack, lookup);
        expect(crumbs).toEqual([
            { index: -1, label: 'Customers' },
            { index: 0, label: 'Orders', detail: 'from Customers #42', frame: stack[0] },
            { index: 1, label: 'Line Items', detail: 'from Orders #7', frame: stack[1] },
        ]);
    });

    it('falls back to the raw table name when lookup returns undefined', () => {
        const stack: DrillFrame[] = [frame({ tableName: 'mystery', filterTable: 'unknown', filterId: '3' })];
        const crumbs = buildDrillCrumbs('customers', stack, () => undefined);
        expect(crumbs[0].label).toBe('customers');
        expect(crumbs[1].label).toBe('mystery');
        expect(crumbs[1].detail).toBe('from unknown #3');
    });

    it('handles self-referential drill crumbs', () => {
        const stack: DrillFrame[] = [
            frame({ tableName: 'categories', filterTable: 'categories', filterId: '1', filterColumn: 'parent_id' }),
            frame({ tableName: 'categories', filterTable: 'categories', filterId: '5', filterColumn: 'parent_id' }),
        ];
        const crumbs = buildDrillCrumbs('categories', stack, lookup);
        expect(crumbs.map((c) => c.label)).toEqual(['Categories', 'Categories', 'Categories']);
        expect(crumbs[1].detail).toBe('from Categories #1');
        expect(crumbs[2].detail).toBe('from Categories #5');
    });

    it('omits detail when the frame has no filter', () => {
        const stack: DrillFrame[] = [frame({ filterTable: undefined, filterId: undefined, filterColumn: undefined })];
        const crumbs = buildDrillCrumbs('customers', stack, lookup);
        expect(crumbs[1].detail).toBeUndefined();
    });
});
