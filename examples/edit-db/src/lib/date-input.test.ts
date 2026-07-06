import { describe, it, expect } from 'vitest';
import type { Column } from '../types/schema';
import {
    isDateColumn,
    isDateTimeColumn,
    toDateInputValue,
    preserveUntouchedDateValues,
} from './date-input';

function col(name: string, opts: Partial<Column> = {}): Column {
    return {
        dbName: name, graphQlName: name, name, label: name, paramType: 'String',
        dbType: 'nvarchar', isPrimaryKey: false, isIdentity: false, isNullable: true,
        isReadOnly: false, metadata: {}, ...opts,
    };
}

const dateTimeCol = (name: string) => col(name, { paramType: 'DateTime', dbType: 'datetime2' });
// Bare-date column: paramType is still DateTime (that's what the server emits),
// with the display format pinned to 'date' via metadata.
const dateCol = (name: string) =>
    col(name, { paramType: 'DateTime', dbType: 'date', metadata: { 'display-format': 'date' } });

describe('isDateColumn / isDateTimeColumn', () => {
    it('recognizes DateTime and DateTime! param types', () => {
        expect(isDateColumn(dateTimeCol('at'))).toBe(true);
        expect(isDateColumn(col('at', { paramType: 'DateTime!', dbType: 'datetime' }))).toBe(true);
        expect(isDateColumn(col('n', { paramType: 'Int' }))).toBe(false);
        expect(isDateColumn(col('s'))).toBe(false);
    });

    it('distinguishes bare dates from datetimes', () => {
        expect(isDateTimeColumn(dateTimeCol('at'))).toBe(true);
        expect(isDateTimeColumn(dateCol('on'))).toBe(false);
    });
});

describe('toDateInputValue', () => {
    it('trims timezone offset and fractional seconds but keeps time-of-day', () => {
        expect(toDateInputValue('2024-03-05T14:30:15.1234567+02:00', true)).toBe('2024-03-05T14:30:15');
        expect(toDateInputValue('2024-03-05T14:30:15Z', true)).toBe('2024-03-05T14:30:15');
    });

    it('normalizes a minutes-only time to include seconds', () => {
        expect(toDateInputValue('2024-03-05T14:30', true)).toBe('2024-03-05T14:30:00');
    });

    it('accepts a space separator', () => {
        expect(toDateInputValue('2024-03-05 14:30:15', true)).toBe('2024-03-05T14:30:15');
    });

    it('returns just the date for bare-date columns', () => {
        expect(toDateInputValue('2024-03-05T14:30:15+02:00', false)).toBe('2024-03-05');
    });

    it('returns the date when a datetime column stores no time part', () => {
        expect(toDateInputValue('2024-03-05', true)).toBe('2024-03-05');
    });

    it("returns '' for empty, zero-date, and unparseable input", () => {
        expect(toDateInputValue(undefined, true)).toBe('');
        expect(toDateInputValue('', true)).toBe('');
        expect(toDateInputValue('0001-01-01T00:00:00', true)).toBe('');
        expect(toDateInputValue('not a date', true)).toBe('');
    });
});

describe('preserveUntouchedDateValues', () => {
    const columns = [dateTimeCol('createdAt'), dateCol('dueOn'), col('title')];

    it('round-trips an offset-carrying datetime unchanged when the field is untouched', () => {
        const raw = '2024-03-05T14:30:15.1234567+02:00';
        const original = { createdAt: raw, dueOn: '2024-04-01', title: 'a' };
        // The form holds the lossy projection (what defaultValues produced).
        const formValues = { createdAt: '2024-03-05T14:30:15', dueOn: '2024-04-01', title: 'edited' };

        const out = preserveUntouchedDateValues(formValues, original, columns);

        expect(out.createdAt).toBe(raw); // stored instant preserved verbatim
        expect(out.dueOn).toBe('2024-04-01');
        expect(out.title).toBe('edited'); // non-date fields untouched by the helper
    });

    it('sends the new value when the user actually changed the field', () => {
        const original = { createdAt: '2024-03-05T14:30:15+02:00' };
        const formValues = { createdAt: '2024-03-06T09:00:00' };

        const out = preserveUntouchedDateValues(formValues, original, columns);

        expect(out.createdAt).toBe('2024-03-06T09:00:00');
    });

    it('restores the raw bare-date value when its projection matches the form', () => {
        const raw = '2024-04-01T00:00:00+02:00';
        const out = preserveUntouchedDateValues({ dueOn: '2024-04-01' }, { dueOn: raw }, columns);
        expect(out.dueOn).toBe(raw);
    });

    it('is a no-op for inserts (empty original row) and null/missing raw values', () => {
        expect(preserveUntouchedDateValues({ createdAt: '' }, {}, columns)).toEqual({ createdAt: '' });
        expect(preserveUntouchedDateValues({ createdAt: '' }, { createdAt: null }, columns))
            .toEqual({ createdAt: '' });
    });

    it('does not mutate the input form values', () => {
        const formValues = { createdAt: '2024-03-05T14:30:15' };
        preserveUntouchedDateValues(formValues, { createdAt: '2024-03-05T14:30:15+02:00' }, columns);
        expect(formValues.createdAt).toBe('2024-03-05T14:30:15');
    });
});
