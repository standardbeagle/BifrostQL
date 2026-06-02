import { describe, it, expect } from 'vitest';
import {
    junctionTableNames,
    detailTabs,
    payloadColumns,
    m2mRowsQuery,
    attachJunctionDetail,
    targetDisplay,
} from './m2m';
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
        expect(query).toContain('student_id: { _eq: $id }');
        // Junction PK + payload selected so we can detach and reveal payload.
        expect(query).toContain('grade');
        // Nested target selection by the junction's target field, with target key + label.
        expect(query).toMatch(/courses\s*\{[^}]*\btitle\b/);
        expect(variables.id).toBe(42);
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
});
