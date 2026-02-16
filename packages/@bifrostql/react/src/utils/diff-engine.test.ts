import { describe, it, expect } from 'vitest';
import { diff, detectConflicts } from './diff-engine';

describe('diff', () => {
  describe('shallow strategy', () => {
    it('detects changed primitive fields', () => {
      const result = diff(
        { name: 'John', age: 30 },
        { name: 'John', age: 31 },
        'shallow',
      );
      expect(result.hasChanges).toBe(true);
      expect(result.changed).toEqual({ age: 31 });
    });

    it('treats all nested objects as changed', () => {
      const address = { city: 'NYC' };
      const result = diff(
        { name: 'John', address },
        { name: 'John', address },
        'shallow',
      );
      expect(result.hasChanges).toBe(false);

      const result2 = diff(
        { name: 'John', address: { city: 'NYC' } },
        { name: 'John', address: { city: 'NYC' } },
        'shallow',
      );
      expect(result2.hasChanges).toBe(true);
      expect(result2.changed).toEqual({ address: { city: 'NYC' } });
    });
  });

  describe('deep strategy', () => {
    it('detects changed primitive fields', () => {
      const result = diff(
        { name: 'John', email: 'old@example.com', age: 30 },
        { name: 'John', email: 'new@example.com', age: 30 },
      );
      expect(result.hasChanges).toBe(true);
      expect(result.changed).toEqual({ email: 'new@example.com' });
    });

    it('returns no changes for identical objects', () => {
      const result = diff({ name: 'John', age: 30 }, { name: 'John', age: 30 });
      expect(result.hasChanges).toBe(false);
      expect(result.changed).toEqual({});
    });

    it('detects deeply nested changes', () => {
      const result = diff(
        { profile: { address: { city: 'NYC', zip: '10001' } } },
        { profile: { address: { city: 'LA', zip: '10001' } } },
      );
      expect(result.hasChanges).toBe(true);
      expect(result.changed).toEqual({
        profile: { address: { city: 'LA', zip: '10001' } },
      });
    });

    it('returns no changes for deeply equal nested objects', () => {
      const result = diff(
        { profile: { address: { city: 'NYC' } } },
        { profile: { address: { city: 'NYC' } } },
      );
      expect(result.hasChanges).toBe(false);
      expect(result.changed).toEqual({});
    });

    it('detects new fields in updated', () => {
      const result = diff({ name: 'John' }, { name: 'John', age: 30 });
      expect(result.hasChanges).toBe(true);
      expect(result.changed).toEqual({ age: 30 });
    });
  });

  describe('array handling', () => {
    it('detects array element additions', () => {
      const result = diff({ tags: ['a', 'b'] }, { tags: ['a', 'b', 'c'] });
      expect(result.hasChanges).toBe(true);
      expect(result.changed).toEqual({ tags: ['a', 'b', 'c'] });
    });

    it('detects array element removals', () => {
      const result = diff({ tags: ['a', 'b', 'c'] }, { tags: ['a', 'c'] });
      expect(result.hasChanges).toBe(true);
      expect(result.changed).toEqual({ tags: ['a', 'c'] });
    });

    it('detects array reordering', () => {
      const result = diff({ tags: ['a', 'b', 'c'] }, { tags: ['c', 'b', 'a'] });
      expect(result.hasChanges).toBe(true);
      expect(result.changed).toEqual({ tags: ['c', 'b', 'a'] });
    });

    it('returns no changes for identical arrays', () => {
      const result = diff({ tags: ['a', 'b'] }, { tags: ['a', 'b'] });
      expect(result.hasChanges).toBe(false);
      expect(result.changed).toEqual({});
    });

    it('handles arrays of objects', () => {
      const result = diff(
        { items: [{ id: 1, name: 'A' }] },
        { items: [{ id: 1, name: 'B' }] },
      );
      expect(result.hasChanges).toBe(true);
      expect(result.changed).toEqual({ items: [{ id: 1, name: 'B' }] });
    });
  });

  describe('edge cases', () => {
    it('handles null values', () => {
      const result = diff({ name: 'John' }, { name: null });
      expect(result.hasChanges).toBe(true);
      expect(result.changed).toEqual({ name: null });
    });

    it('handles change from null to value', () => {
      const result = diff({ name: null }, { name: 'John' });
      expect(result.hasChanges).toBe(true);
      expect(result.changed).toEqual({ name: 'John' });
    });

    it('handles type changes', () => {
      const result = diff({ value: '42' as unknown }, { value: 42 as unknown });
      expect(result.hasChanges).toBe(true);
      expect(result.changed).toEqual({ value: 42 });
    });

    it('handles empty objects', () => {
      const result = diff({}, {});
      expect(result.hasChanges).toBe(false);
      expect(result.changed).toEqual({});
    });
  });
});

describe('detectConflicts', () => {
  it('detects conflicting changes to the same field', () => {
    const base = { name: 'John', age: 30 };
    const current = { name: 'Jane', age: 30 };
    const incoming = { name: 'Jack', age: 30 };

    const conflicts = detectConflicts(base, current, incoming);
    expect(conflicts).toEqual(['name']);
  });

  it('allows non-overlapping changes', () => {
    const base = { name: 'John', age: 30 };
    const current = { name: 'Jane', age: 30 };
    const incoming = { name: 'John', age: 31 };

    const conflicts = detectConflicts(base, current, incoming);
    expect(conflicts).toEqual([]);
  });

  it('allows identical changes to the same field', () => {
    const base = { name: 'John', age: 30 };
    const current = { name: 'Jane', age: 30 };
    const incoming = { name: 'Jane', age: 30 };

    const conflicts = detectConflicts(base, current, incoming);
    expect(conflicts).toEqual([]);
  });

  it('detects multiple conflicting fields', () => {
    const base = { name: 'John', email: 'john@a.com', age: 30 };
    const current = { name: 'Jane', email: 'jane@a.com', age: 30 };
    const incoming = { name: 'Jack', email: 'jack@a.com', age: 31 };

    const conflicts = detectConflicts(base, current, incoming);
    expect(conflicts).toEqual(['name', 'email']);
  });
});
