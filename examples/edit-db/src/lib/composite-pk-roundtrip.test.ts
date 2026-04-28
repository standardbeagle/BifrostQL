/**
 * End-to-end composite-PK round-trip test. Verifies that the layered helpers in
 * row-id.ts and query-builder.ts agree on variable naming, filter shape, and coercion
 * so a caller can construct a full-featured by-id GraphQL query from a junction-table
 * row without stitching the pieces together manually.
 *
 * Flow:
 *   row → rowIdOf(row, table) → stable string id
 *   id  → parsePkRoute(id, table) → decoded PkFilter
 *   filter → buildPkEqFilter(filter, table) → GraphQL filterText + params + variables
 *   id, table, schema → buildQuery(... id ...) emits a full query string
 *   id, table → buildPkEqVariables(id, table) produces the variables dict
 *
 * The two parallel "variables" paths (buildPkEqFilter and buildPkEqVariables) must
 * produce byte-identical dicts for the by-own-PK path. The query string must reference
 * every variable in its params list. For a composite PK we additionally assert that
 * student_id=1/course_id=cs-202 survives the round-trip even though student_id=1 alone
 * would collide with another row in the fixture.
 */
import { describe, it, expect } from 'vitest';
import {
    rowIdOf,
    parsePkRoute,
    buildPkEqFilter,
    encodePkRoute,
    pkFilterFor,
} from './row-id';
import {
    buildQuery,
    buildPkEqVariables,
    getPkTypes,
    type PkTypeInfo,
} from './query-builder';
import type { Column, Schema, Table } from '../types/schema';

function col(name: string, paramType: string, isPrimaryKey = false): Column {
    return {
        dbName: name,
        graphQlName: name,
        name,
        label: name,
        paramType,
        dbType: '',
        isPrimaryKey,
        isIdentity: false,
        isNullable: false,
        isReadOnly: false,
        metadata: {},
    };
}

function table(name: string, primaryKeys: string[], columns: Column[]): Table {
    return {
        dbName: name,
        graphQlName: name,
        name,
        label: name,
        labelColumn: 'grade',
        primaryKeys,
        isEditable: true,
        metadata: {},
        columns,
        multiJoins: [],
        singleJoins: [],
    };
}

function schemaOf(tables: Table[]): Schema {
    return {
        loading: false,
        error: null,
        data: tables,
        findTable: (name: string) => tables.find((t) => t.name === name || t.graphQlName === name),
    };
}

const enrollment = table(
    'enrollment',
    ['student_id', 'course_id'],
    [
        col('student_id', 'Int!', true),
        col('course_id', 'String!', true),
        col('grade', 'String'),
    ],
);

const junctionSchema = schemaOf([enrollment]);

// Two rows collide on student_id alone — the legacy single-PK keying would have
// dropped one of them at render time.
const rowA = { student_id: 1, course_id: 'cs-101', grade: 'A' };
const rowB = { student_id: 1, course_id: 'cs-202', grade: 'B+' };
const rowC = { student_id: 2, course_id: 'cs-101', grade: 'A-' };

describe('composite PK round-trip', () => {
    it('produces unique rowIds for rows that share the first PK column', () => {
        const ids = [rowA, rowB, rowC].map((r, i) => rowIdOf(r, enrollment, i));
        expect(new Set(ids).size).toBe(3);
        expect(ids).toEqual(['1::cs-101', '1::cs-202', '2::cs-101']);
    });

    it('round-trips a row via encodePkRoute + parsePkRoute back to the same PK values', () => {
        const filter = pkFilterFor(rowB, enrollment);
        expect(filter).toEqual({ student_id: 1, course_id: 'cs-202' });
        const encoded = encodePkRoute(filter!, enrollment);
        expect(encoded).toBe('1::cs-202');
        expect(parsePkRoute(encoded, enrollment)).toEqual({ student_id: '1', course_id: 'cs-202' });
    });

    it('buildPkEqFilter and buildPkEqVariables emit matching variable dicts', () => {
        const routeId = '1::cs-202';
        const parsed = parsePkRoute(routeId, enrollment)!;
        const eqFilter = buildPkEqFilter(parsed, enrollment)!;
        const viaVariables = buildPkEqVariables(routeId, enrollment);

        expect(eqFilter.variables).toEqual(viaVariables);
        expect(viaVariables).toEqual({ pk_student_id: 1, pk_course_id: 'cs-202' });
    });

    it('buildQuery emits a query whose variables match buildPkEqVariables exactly', () => {
        const routeId = '1::cs-202';
        const query = buildQuery(enrollment, junctionSchema, '', [], routeId)!;
        const variables = buildPkEqVariables(routeId, enrollment);

        // Every variable name referenced in the query must appear in the variables dict.
        for (const name of Object.keys(variables)) {
            expect(query).toContain(`$${name}`);
        }
        // The composite AND clause should appear verbatim.
        expect(query).toContain(
            '{and: [{student_id: {_eq: $pk_student_id}}, {course_id: {_eq: $pk_course_id}}]}',
        );
    });

    it('expands all PK columns via getPkTypes in declaration order with correct GraphQL types', () => {
        const types: PkTypeInfo[] = getPkTypes(enrollment);
        expect(types).toEqual([
            { name: 'student_id', gqlType: 'Int' },
            { name: 'course_id', gqlType: 'String' },
        ]);
    });

    it('preserves single-PK query shape (no composite AND wrapping)', () => {
        const users = table(
            'users',
            ['id'],
            [col('id', 'Int!', true), col('name', 'String')],
        );
        const query = buildQuery(users, schemaOf([users]), '', [], '42')!;
        // Single-PK stays on the byte-identical legacy path with $id.
        expect(query).toContain('$id: Int');
        expect(query).toContain('{ id: { _eq: $id}}');
        expect(query).not.toContain('$pk_id');
        expect(query).not.toContain('{and:');
    });
});
