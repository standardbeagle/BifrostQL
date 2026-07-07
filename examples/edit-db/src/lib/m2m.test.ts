import { describe, it, expect } from 'vitest';
import {
    junctionTableNames,
    detailTabs,
    payloadColumns,
    m2mRowsQuery,
    m2mTargetPickerPlan,
    attachJunctionDetail,
    targetDisplay,
} from './m2m';
import { rowIdOf } from './row-id';
import type { Column, Join, ManyToManyJoin, Table } from '../types/schema';

function col(name: string, opts: Partial<Column> = {}): Column {
    return {
        dbName: name,
        graphQlName: name,
        name,
        label: name,
        paramType: 'String',
        dbType: 'nvarchar',
        isPrimaryKey: false,
        isIdentity: false,
        isNullable: true,
        isReadOnly: false,
        metadata: {},
        ...opts,
    };
}

function table(name: string, opts: Partial<Table> = {}): Table {
    return {
        dbName: name,
        graphQlName: name,
        name,
        label: name,
        labelColumn: 'name',
        primaryKeys: ['id'],
        isEditable: true,
        metadata: {},
        columns: [],
        multiJoins: [],
        singleJoins: [],
        ...opts,
    };
}

const m2m: ManyToManyJoin = {
    name: 'enrollments',
    targetTable: 'courses',
    junctionTable: 'enrollments',
    junctionTargetField: 'courses',
    sourceColumnNames: ['id'],
    junctionSourceColumnNames: ['student_id'],
    junctionTargetColumnNames: ['course_id'],
    targetColumnNames: ['id'],
    hasPayload: true,
};

const childJoin: Join = {
    name: 'addresses',
    sourceColumnNames: ['id'],
    destinationTable: 'addresses',
    destinationColumnNames: ['student_id'],
};

const junctionJoin: Join = {
    name: 'enrollments',
    sourceColumnNames: ['id'],
    destinationTable: 'enrollments',
    destinationColumnNames: ['student_id'],
};

describe('junctionTableNames', () => {
    it('collects the junction table of every m2m join', () => {
        const t = table('students', { manyToManyJoins: [m2m] });
        expect(junctionTableNames(t).has('enrollments')).toBe(true);
    });

    it('is empty when there are no m2m joins', () => {
        expect(junctionTableNames(table('students')).size).toBe(0);
    });
});

describe('detailTabs', () => {
    it('keeps non-junction children and adds one tab per m2m, suppressing the junction', () => {
        const t = table('students', {
            multiJoins: [childJoin, junctionJoin],
            manyToManyJoins: [m2m],
        });

        const tabs = detailTabs(t);

        // The raw junction multiJoin is folded into the m2m tab, not shown twice.
        expect(tabs).toHaveLength(2);
        const child = tabs.find((x) => x.kind === 'child');
        const link = tabs.find((x) => x.kind === 'm2m');
        expect(child?.kind === 'child' && child.join.destinationTable).toBe('addresses');
        expect(link?.kind === 'm2m' && link.m2m.targetTable).toBe('courses');
    });

    it('falls back to plain children when manyToManyJoins is absent', () => {
        const t = table('students', { multiJoins: [childJoin] });
        const tabs = detailTabs(t);
        expect(tabs).toHaveLength(1);
        expect(tabs[0].kind).toBe('child');
    });
});

describe('payloadColumns', () => {
    it('returns junction columns that are neither PK nor either FK', () => {
        const junction = table('enrollments', {
            primaryKeys: ['id'],
            columns: [
                col('id', { isPrimaryKey: true }),
                col('student_id'),
                col('course_id'),
                col('grade'),
                col('enrolled_on', { paramType: 'DateTime' }),
            ],
        });

        const names = payloadColumns(junction, m2m).map((c) => c.name);
        expect(names).toEqual(['grade', 'enrolled_on']);
    });

    it('is empty for a pure junction', () => {
        const junction = table('enrollments', {
            columns: [col('id', { isPrimaryKey: true }), col('student_id'), col('course_id')],
        });
        expect(payloadColumns(junction, m2m)).toHaveLength(0);
    });
});

describe('m2mRowsQuery', () => {
    const junction = table('enrollments', {
        columns: [
            col('id', { isPrimaryKey: true }),
            col('student_id', { paramType: 'Int' }),
            col('course_id', { paramType: 'Int' }),
            col('grade'),
        ],
    });
    const target = table('courses', { labelColumn: 'title', primaryKeys: ['id'] });

    it('filters junction rows by the parent key and nests the target selection', () => {
        const { query, variables } = m2mRowsQuery(junction, target, m2m, '42');

        // Paged junction query scoped to the parent via the junction source FK.
        expect(query).toContain('enrollments(');
        expect(query).toContain('student_id: { _eq: $src0 }');
        // Junction PK + payload selected so we can detach and reveal payload.
        expect(query).toContain('grade');
        // Nested target selection by the junction's target field, with target key + label.
        expect(query).toMatch(/courses\s*\{[^}]*\btitle\b/);
        expect(variables.src0).toBe(42);
    });

    it('constrains every column of a composite junction source FK', () => {
        // A junction whose source FK spans two columns. Filtering on only the first
        // (school_id) would match another student's rows that share the same school —
        // detaching then deletes the wrong parent's link.
        const compositeJunction = table('enrollments', {
            columns: [
                col('id', { isPrimaryKey: true }),
                col('school_id', { paramType: 'Int' }),
                col('student_no', { paramType: 'Int' }),
                col('course_id', { paramType: 'Int' }),
            ],
        });
        const compositeM2m: ManyToManyJoin = {
            ...m2m,
            sourceColumnNames: ['school_id', 'student_no'],
            junctionSourceColumnNames: ['school_id', 'student_no'],
        };

        const { query, variables } = m2mRowsQuery(compositeJunction, target, compositeM2m, '7::42');

        // Both source columns are constrained, combined with `and`.
        expect(query).toContain('and: [');
        expect(query).toContain('school_id: { _eq: $src0 }');
        expect(query).toContain('student_no: { _eq: $src1 }');
        expect(variables.src0).toBe(7);
        expect(variables.src1).toBe(42);
    });

    it('pairs each junction source column with the route part at the same index', () => {
        // Guards the composite column-order invariant documented in m2mRowsQuery:
        // junctionSourceColumnNames[i] is bound to the i-th route part. If the pairing
        // ever drifted (e.g. reversed), src0 would carry the second value and the
        // wrong parent's junction rows would be listed/detached. Distinct type-coerced
        // values (100 vs 200) make an index swap unambiguous.
        const compositeJunction = table('enrollments', {
            columns: [
                col('id', { isPrimaryKey: true }),
                col('school_id', { paramType: 'Int' }),
                col('student_no', { paramType: 'Int' }),
                col('course_id', { paramType: 'Int' }),
            ],
        });
        const compositeM2m: ManyToManyJoin = {
            ...m2m,
            sourceColumnNames: ['school_id', 'student_no'],
            junctionSourceColumnNames: ['school_id', 'student_no'],
        };

        const { query, variables } = m2mRowsQuery(compositeJunction, target, compositeM2m, '100::200');

        // First junction source column ↔ first route part; second ↔ second.
        expect(query).toContain('school_id: { _eq: $src0 }');
        expect(query).toContain('student_no: { _eq: $src1 }');
        expect(variables.src0).toBe(100);
        expect(variables.src1).toBe(200);
    });

    it('coerces Boolean junction source keys using the shared GraphQL coercion rules', () => {
        const booleanJunction = table('feature_courses', {
            columns: [
                col('id', { isPrimaryKey: true }),
                col('is_featured', { paramType: 'Boolean' }),
                col('course_id', { paramType: 'Int' }),
            ],
        });
        const booleanM2m: ManyToManyJoin = {
            ...m2m,
            sourceColumnNames: ['is_featured'],
            junctionSourceColumnNames: ['is_featured'],
        };

        const { variables } = m2mRowsQuery(booleanJunction, target, booleanM2m, 'true');

        expect(variables.src0).toBe(true);
    });

    it('throws when a junction source column is missing from the junction schema', () => {
        // Schema drift: relationship metadata names a junction column the table
        // doesn't have. Defaulting its type would coerce a GUID key to NaN and
        // silently return zero links, so this must fail loudly instead.
        const driftedJunction = table('enrollments', {
            columns: [col('id', { isPrimaryKey: true }), col('course_id', { paramType: 'Int' })],
        });

        expect(() => m2mRowsQuery(driftedJunction, target, m2m, '42'))
            .toThrow(/Junction table 'enrollments' has no column 'student_id'/);
    });
});

describe('m2mTargetPickerPlan', () => {
    it('builds a target picker query with an enum sort value', () => {
        const target = table('courses', {
            labelColumn: 'title',
            primaryKeys: ['id'],
            columns: [col('id', { paramType: 'Int', isPrimaryKey: true }), col('title')],
        });
        const { query, idColumn } = m2mTargetPickerPlan(target, m2m);

        expect(idColumn).toBe('id');
        expect(query).toContain('sort: [title_asc]');
        expect(query).not.toContain('sort: ["title_asc"]');
        expect(query).toContain('data { id label: title }');
    });

    it('falls back to the id column when the target has no label column', () => {
        const target = table('courses', {
            labelColumn: '',
            primaryKeys: ['id'],
            columns: [col('id', { paramType: 'Int', isPrimaryKey: true })],
        });
        const { query } = m2mTargetPickerPlan(target, m2m);

        expect(query).toContain('sort: [id_asc]');
        expect(query).toContain('data { id }');
    });

    it('emits a server-side _contains filter for a String label column', () => {
        const target = table('courses', {
            labelColumn: 'title',
            primaryKeys: ['id'],
            columns: [col('title', { paramType: 'String' })],
        });
        const plan = m2mTargetPickerPlan(target, m2m, 'alg');

        expect(plan.serverSearch).toBe(true);
        expect(plan.query).toContain('$search: String');
        expect(plan.query).toContain('filter: {title: {_contains: $search}}');
    });

    it('does not emit a server filter when the label column is not String', () => {
        const target = table('courses', {
            labelColumn: 'code',
            primaryKeys: ['id'],
            columns: [col('code', { paramType: 'Int' })],
        });
        const plan = m2mTargetPickerPlan(target, m2m, 'alg');

        expect(plan.serverSearch).toBe(false);
        expect(plan.query).not.toContain('$search');
        expect(plan.query).not.toContain('_contains');
    });

    it('throws when the label column is missing from the target schema', () => {
        // Schema drift: metadata names a label column the target table doesn't
        // have — fail loudly rather than silently degrading the picker.
        const target = table('courses', {
            labelColumn: 'title',
            primaryKeys: ['id'],
            columns: [col('id', { paramType: 'Int', isPrimaryKey: true })],
        });

        expect(() => m2mTargetPickerPlan(target, m2m))
            .toThrow(/Target table 'courses' has no column 'title'/);
    });

    it('rejects unsafe target label columns before building GraphQL', () => {
        const target = table('courses', {
            labelColumn: 'title) { injected',
            primaryKeys: ['id'],
            columns: [col('id', { paramType: 'Int', isPrimaryKey: true })],
        });

        expect(() => m2mTargetPickerPlan(target, m2m))
            .toThrow(/Invalid GraphQL many-to-many target label column/);
    });
});

describe('targetDisplay', () => {
    const target = table('courses', { labelColumn: 'title', primaryKeys: ['id'] });

    it('reads the nested target id and label from a junction row', () => {
        const junctionRow = { id: 5, grade: 'A', courses: { id: 99, label: 'Algebra' } };
        expect(targetDisplay(junctionRow, m2m, target)).toEqual({ id: '99', label: 'Algebra' });
    });

    it('falls back to the id when the label is absent', () => {
        const junctionRow = { id: 5, courses: { id: 99 } };
        expect(targetDisplay(junctionRow, m2m, target)).toEqual({ id: '99', label: '99' });
    });

    it('returns null when the nested target is missing', () => {
        expect(targetDisplay({ id: 5 }, m2m, target)).toBeNull();
    });
});

describe('attachJunctionDetail', () => {
    it('maps parent + target keys onto the junction FK columns', () => {
        const detail = attachJunctionDetail(m2m, '7', '99');
        expect(detail).toEqual({ student_id: '7', course_id: '99' });
    });

    it('decodes a route-encoded parent id so the junction column gets the real DB value', () => {
        // parentId is route-encoded (encodeURIComponent); the read side
        // (m2mRowsQuery) filters on the decoded value, so writing the encoded
        // form would insert a dangling link the panel never shows.
        const detail = attachJunctionDetail(m2m, 'caf%C3%A9', '99');
        expect(detail).toEqual({ student_id: 'café', course_id: '99' });
    });

    it('writes the target id verbatim — picker ids are raw, not route-encoded', () => {
        const detail = attachJunctionDetail(m2m, '7', 'a%20b');
        expect(detail).toEqual({ student_id: '7', course_id: 'a%20b' });
    });

    it('round-trips a parent value written by rowIdOf back to the original', () => {
        const parent = table('students', { primaryKeys: ['code'] });
        const routeId = rowIdOf({ code: 'a/b::c d' }, parent, 0);
        const detail = attachJunctionDetail(m2m, routeId, '1');
        expect(detail.student_id).toBe('a/b::c d');
    });

    it('rejects composite junction FKs loudly', () => {
        const composite: ManyToManyJoin = {
            ...m2m,
            junctionSourceColumnNames: ['student_id', 'term_id'],
        };
        expect(() => attachJunctionDetail(composite, '1::2', '9')).toThrow(/single-column/);
    });
});
