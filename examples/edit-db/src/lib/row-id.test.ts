import { describe, it, expect } from 'vitest';
import {
    rowIdOf,
    pkFilterFor,
    buildPkEqFilter,
    encodePkRoute,
    parsePkRoute,
    type PkFilter,
} from './row-id';
import type { Column, Table } from '../types/schema';

function col(name: string, paramType: string): Column {
    return {
        dbName: name,
        graphQlName: name,
        name,
        label: name,
        paramType,
        dbType: '',
        isPrimaryKey: true,
        isIdentity: false,
        isNullable: false,
        isReadOnly: false,
        metadata: {},
    };
}

function tbl(primaryKeys: string[], columns: Column[] = []): Pick<Table, 'primaryKeys' | 'columns'> {
    return { primaryKeys, columns };
}

describe('rowIdOf', () => {
    it('returns the PK value as-is for a single-column PK', () => {
        expect(rowIdOf({ id: 1 }, tbl(['id']), 0)).toBe('1');
    });

    it('joins composite PK values with "::"', () => {
        expect(rowIdOf({ a: 1, b: 'x' }, tbl(['a', 'b']), 0)).toBe('1::x');
    });

    it('treats null PK value as empty string but preserves delimiter position', () => {
        expect(rowIdOf({ a: 1, b: null }, tbl(['a', 'b']), 0)).toBe('1::');
        expect(rowIdOf({ a: null, b: 2 }, tbl(['a', 'b']), 0)).toBe('::2');
    });

    it('returns row-${index} when the table has no primary keys', () => {
        expect(rowIdOf({ id: 1 }, tbl([]), 0)).toBe('row-0');
        expect(rowIdOf({ id: 1 }, tbl([]), 7)).toBe('row-7');
    });

    it('returns row-${index} when primaryKeys is undefined', () => {
        expect(rowIdOf({ id: 1 }, { primaryKeys: undefined as unknown as string[] }, 3)).toBe('row-3');
    });

    it('escapes PK values containing "::" so the delimiter is unambiguous', () => {
        // Value "a::b" encoded as "a%3A%3Ab" — no raw "::" in the encoded value.
        const encoded = rowIdOf({ a: 'a::b', b: 'y' }, tbl(['a', 'b']), 0);
        expect(encoded).toBe('a%3A%3Ab::y');
        expect(encoded.indexOf('::')).toBe(encoded.lastIndexOf('::'));
    });

    it('handles undefined row safely', () => {
        expect(rowIdOf(null, tbl(['id']), 0)).toBe('');
        expect(rowIdOf(undefined, tbl([]), 5)).toBe('row-5');
    });

    it('converts numbers, booleans, and BigInts to strings', () => {
        expect(rowIdOf({ id: 42 }, tbl(['id']), 0)).toBe('42');
        expect(rowIdOf({ flag: true }, tbl(['flag']), 0)).toBe('true');
    });
});

describe('pkFilterFor', () => {
    it('returns all PK columns for a composite PK', () => {
        const filter = pkFilterFor({ a: 1, b: 'x', other: 'ignored' }, tbl(['a', 'b']));
        expect(filter).toEqual({ a: 1, b: 'x' });
    });

    it('returns the single PK column for a simple PK', () => {
        expect(pkFilterFor({ id: 5, name: 'ignored' }, tbl(['id']))).toEqual({ id: 5 });
    });

    it('returns null when a PK column is missing from the row', () => {
        expect(pkFilterFor({ a: 1 }, tbl(['a', 'b']))).toBeNull();
    });

    it('includes null PK values in the filter (null is a valid PK value here)', () => {
        expect(pkFilterFor({ a: 1, b: null }, tbl(['a', 'b']))).toEqual({ a: 1, b: null });
    });

    it('returns null when the table has no primary keys', () => {
        expect(pkFilterFor({ id: 1 }, tbl([]))).toBeNull();
    });

    it('returns null for a null row', () => {
        expect(pkFilterFor(null, tbl(['id']))).toBeNull();
    });
});

describe('buildPkEqFilter', () => {
    it('produces an unwrapped filter for a single PK', () => {
        const result = buildPkEqFilter({ id: 42 }, tbl(['id'], [col('id', 'Int!')]));
        expect(result).not.toBeNull();
        expect(result!.filterText).toBe('{id: {_eq: $pk_id}}');
        expect(result!.variables).toEqual({ pk_id: 42 });
        expect(result!.params).toEqual(['$pk_id: Int']);
    });

    it('wraps a composite PK filter in {and: [...]} with one variable per column', () => {
        const result = buildPkEqFilter(
            { student_id: 1, course_id: 'cs-101' },
            tbl(['student_id', 'course_id'], [col('student_id', 'Int!'), col('course_id', 'String!')]),
        );
        expect(result).not.toBeNull();
        expect(result!.filterText).toBe(
            '{and: [{student_id: {_eq: $pk_student_id}}, {course_id: {_eq: $pk_course_id}}]}',
        );
        expect(result!.variables).toEqual({ pk_student_id: 1, pk_course_id: 'cs-101' });
        expect(result!.params).toEqual(['$pk_student_id: Int', '$pk_course_id: String']);
    });

    it('coerces Int columns from string input', () => {
        const result = buildPkEqFilter({ id: '42' }, tbl(['id'], [col('id', 'Int!')]));
        expect(result!.variables).toEqual({ pk_id: 42 });
    });

    it('coerces Float columns from string input', () => {
        const result = buildPkEqFilter({ price: '3.14' }, tbl(['price'], [col('price', 'Float!')]));
        expect(result!.variables).toEqual({ pk_price: 3.14 });
    });

    it('leaves String PK values untouched', () => {
        const result = buildPkEqFilter({ code: 'CS-101' }, tbl(['code'], [col('code', 'String!')]));
        expect(result!.variables).toEqual({ pk_code: 'CS-101' });
    });

    it('defaults to String type when column metadata is missing', () => {
        const result = buildPkEqFilter({ unknown: 'x' }, tbl(['unknown'], []));
        expect(result!.params).toEqual(['$pk_unknown: String']);
    });

    it('returns null when PK is missing from the row', () => {
        const result = buildPkEqFilter({ a: 1 }, tbl(['a', 'b'], [col('a', 'Int!'), col('b', 'Int!')]));
        expect(result).toBeNull();
    });
});

describe('encodePkRoute / parsePkRoute round-trip', () => {
    it('round-trips a single-column PK', () => {
        const table = tbl(['id']);
        const filter: PkFilter = { id: '42' };
        const encoded = encodePkRoute(filter, table);
        expect(encoded).toBe('42');
        expect(parsePkRoute(encoded, table)).toEqual({ id: '42' });
    });

    it('round-trips a composite PK', () => {
        const table = tbl(['a', 'b']);
        const filter: PkFilter = { a: '1', b: 'x' };
        const encoded = encodePkRoute(filter, table);
        expect(encoded).toBe('1::x');
        expect(parsePkRoute(encoded, table)).toEqual({ a: '1', b: 'x' });
    });

    it('round-trips PK values containing the delimiter', () => {
        const table = tbl(['a', 'b']);
        const filter: PkFilter = { a: 'a::b', b: 'x' };
        const encoded = encodePkRoute(filter, table);
        expect(parsePkRoute(encoded, table)).toEqual({ a: 'a::b', b: 'x' });
    });

    it('round-trips PK values containing slashes and reserved URL chars', () => {
        const table = tbl(['path']);
        const filter: PkFilter = { path: '/foo/bar?x=1' };
        const encoded = encodePkRoute(filter, table);
        expect(parsePkRoute(encoded, table)).toEqual({ path: '/foo/bar?x=1' });
    });

    it('round-trips null values as null', () => {
        const table = tbl(['a', 'b']);
        const encoded = encodePkRoute({ a: 1, b: null }, table);
        expect(encoded).toBe('1::');
        expect(parsePkRoute(encoded, table)).toEqual({ a: '1', b: null });
    });

    it('returns null when parsing the wrong number of segments', () => {
        expect(parsePkRoute('1::2::3', tbl(['a', 'b']))).toBeNull();
    });

    it('returns null when parsing against a PK-less table', () => {
        expect(parsePkRoute('anything', tbl([]))).toBeNull();
    });

    it('encodes an empty string when the table has no primary keys', () => {
        expect(encodePkRoute({ id: 1 }, tbl([]))).toBe('');
    });
});
