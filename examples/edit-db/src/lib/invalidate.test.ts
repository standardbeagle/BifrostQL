import { describe, it, expect } from 'vitest';
import { QueryClient } from '@tanstack/react-query';
import { invalidateAfterTableWrite } from './invalidate';

function seed(client: QueryClient, keys: readonly (readonly unknown[])[]) {
    for (const key of keys) client.setQueryData(key as unknown[], {});
}

function isInvalidated(client: QueryClient, key: readonly unknown[]): boolean {
    return client.getQueryState(key as unknown[])?.isInvalidated ?? false;
}

describe('invalidateAfterTableWrite', () => {
    it('invalidates every cross-table family regardless of which table the query targets', () => {
        const client = new QueryClient();
        const keys = [
            ['tableData', 'users', 'query-text', { limit: 50 }],
            ['tableData', 'orders', 'query-text', { limit: 50 }], // other table: joined labels can embed users
            ['editRecord', 'users', '1'],
            ['editRecord', 'orders', '7'], // other table's edit dialog can show users labels
            ['tableRowCounts', ['users', 'orders']],
            ['m2mRows', 'user_roles', 'roles', '1'],
            ['fkPreview', 'users', 'user_id:1'],
        ] as const;
        seed(client, keys);

        invalidateAfterTableWrite(client, 'users');

        for (const key of keys) expect(isInvalidated(client, key), JSON.stringify(key)).toBe(true);
    });

    it('invalidates table-scoped families only for the written table', () => {
        const client = new QueryClient();
        seed(client, [
            ['tableRef', 'users', 'name', ''],
            ['tableRef', 'orders', 'sku', ''],
            ['tableRefValue', 'users', 'name', '1'],
            ['tableRefValue', 'orders', 'sku', '9'],
            ['compositeTableRef', 'users', 'a|b'],
            ['m2mTargets', 'users', ''],
            ['m2mTargets', 'orders', ''],
        ]);

        invalidateAfterTableWrite(client, 'users');

        expect(isInvalidated(client, ['tableRef', 'users', 'name', ''])).toBe(true);
        expect(isInvalidated(client, ['tableRefValue', 'users', 'name', '1'])).toBe(true);
        expect(isInvalidated(client, ['compositeTableRef', 'users', 'a|b'])).toBe(true);
        expect(isInvalidated(client, ['m2mTargets', 'users', ''])).toBe(true);
        expect(isInvalidated(client, ['tableRef', 'orders', 'sku', ''])).toBe(false);
        expect(isInvalidated(client, ['tableRefValue', 'orders', 'sku', '9'])).toBe(false);
        expect(isInvalidated(client, ['m2mTargets', 'orders', ''])).toBe(false);
    });

    it('leaves unrelated families and malformed keys untouched', () => {
        const client = new QueryClient();
        seed(client, [
            ['schema'],
            ['somethingElse', 'users'],
            [{ not: 'a-string' }, 'users'],
        ]);

        invalidateAfterTableWrite(client, 'users');

        expect(isInvalidated(client, ['schema'])).toBe(false);
        expect(isInvalidated(client, ['somethingElse', 'users'])).toBe(false);
        expect(isInvalidated(client, [{ not: 'a-string' }, 'users'])).toBe(false);
    });
});
