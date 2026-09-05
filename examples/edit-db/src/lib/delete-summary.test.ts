import { describe, it, expect } from 'vitest';
import { describeDeleteRows } from './delete-summary';

const rows = [
    { id: 1, firstName: 'James', lastName: 'Wilson', accessId: 'PART001', notes: 'x'.repeat(80) },
    { id: 2, firstName: 'Maria', lastName: 'Garcia', accessId: 'PART002', notes: '' },
];

describe('describeDeleteRows', () => {
    it('names the row so the reader can see which record is going', () => {
        const { labels, remaining } = describeDeleteRows([{ id: 1 }], rows);
        expect(labels).toEqual(['1 — James Wilson']);
        expect(remaining).toBe(0);
    });

    it('skips prose columns, which identify nothing at a glance', () => {
        const { labels } = describeDeleteRows([{ id: 1 }], rows, { maxDescriptors: 5 });
        expect(labels[0]).not.toContain('xxx');
        expect(labels[0]).toBe('1 — James Wilson PART001');
    });

    it('skips empty values rather than rendering a gap', () => {
        const { labels } = describeDeleteRows([{ id: 2 }], rows, { maxDescriptors: 3 });
        expect(labels[0]).toBe('2 — Maria Garcia PART002');
    });

    it('falls back to the key when the row is not on the current page', () => {
        const { labels } = describeDeleteRows([{ id: 99 }], rows);
        expect(labels).toEqual(['99']);
    });

    it('handles composite keys', () => {
        const composite = [{ coachId: 7, workshopId: 3, role: 'lead' }];
        const { labels } = describeDeleteRows([{ coachId: 7, workshopId: 3 }], composite);
        expect(labels).toEqual(['7, 3 — lead']);
    });

    it('caps the list and reports the remainder', () => {
        const many = Array.from({ length: 9 }, (_, i) => ({ id: i + 1 }));
        const { labels, remaining } = describeDeleteRows(many, []);
        expect(labels).toHaveLength(5);
        expect(remaining).toBe(4);
    });

    it('returns nothing to show for an empty selection', () => {
        expect(describeDeleteRows([], rows)).toEqual({ labels: [], remaining: 0 });
    });
});
