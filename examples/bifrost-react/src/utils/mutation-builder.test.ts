import { describe, it, expect } from 'vitest';
import {
  buildMutation,
  buildInsertMutation,
  buildUpdateMutation,
  buildDeleteMutation,
} from './mutation-builder';

describe('buildMutation', () => {
  it('builds an insert mutation for a table', () => {
    const result = buildMutation('users', 'insert');
    expect(result).toBe(
      'mutation Insert($detail: Insert_users) { users(insert: $detail) }',
    );
  });

  it('builds an update mutation for a table', () => {
    const result = buildMutation('users', 'update');
    expect(result).toBe(
      'mutation Update($detail: Update_users) { users(update: $detail) }',
    );
  });

  it('builds a delete mutation for a table', () => {
    const result = buildMutation('users', 'delete');
    expect(result).toBe(
      'mutation Delete($detail: Delete_users) { users(delete: $detail) }',
    );
  });
});

describe('buildInsertMutation', () => {
  it('generates the correct insert mutation string', () => {
    const result = buildInsertMutation('orders');
    expect(result).toContain('Insert_orders');
    expect(result).toContain('orders(insert: $detail)');
    expect(result).toContain('mutation Insert');
  });

  it('preserves table name exactly as given', () => {
    const result = buildInsertMutation('order_items');
    expect(result).toContain('Insert_order_items');
    expect(result).toContain('order_items(insert: $detail)');
  });
});

describe('buildUpdateMutation', () => {
  it('generates the correct update mutation string', () => {
    const result = buildUpdateMutation('products');
    expect(result).toContain('Update_products');
    expect(result).toContain('products(update: $detail)');
    expect(result).toContain('mutation Update');
  });
});

describe('buildDeleteMutation', () => {
  it('generates the correct delete mutation string', () => {
    const result = buildDeleteMutation('sessions');
    expect(result).toContain('Delete_sessions');
    expect(result).toContain('sessions(delete: $detail)');
    expect(result).toContain('mutation Delete');
  });
});
