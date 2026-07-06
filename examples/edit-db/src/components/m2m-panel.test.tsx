import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import '@testing-library/jest-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { Column, ManyToManyJoin, Table } from '../types/schema';

// Link renders an anchor without router context in tests.
vi.mock('../hooks/usePath', () => ({
    Link: ({ children }: { children: React.ReactNode }) => <a>{children}</a>,
}));

const findTable = vi.fn();
vi.mock('../hooks/useSchema', () => ({ useSchema: () => ({ findTable }) }));

const fetcherQuery = vi.fn();
vi.mock('../common/fetcher', () => ({ useFetcher: () => ({ query: fetcherQuery }) }));

const deleteRow = vi.fn().mockResolvedValue({});
vi.mock('../hooks/useDeleteMutation', () => ({
    useDeleteMutation: () => ({ deleteRow, deleteRows: vi.fn(), isPending: false, error: null }),
}));

const insert = vi.fn().mockResolvedValue({});
vi.mock('../hooks/useTableMutation', () => ({
    useTableMutation: () => ({ insert, update: vi.fn(), isPending: false, error: null }),
}));

import { M2mPanel } from './m2m-panel';

function col(name: string, opts: Partial<Column> = {}): Column {
    return {
        dbName: name, graphQlName: name, name, label: name, paramType: 'String',
        dbType: 'nvarchar', isPrimaryKey: false, isIdentity: false, isNullable: true,
        isReadOnly: false, metadata: {}, ...opts,
    };
}
function table(name: string, opts: Partial<Table> = {}): Table {
    return {
        dbName: name, graphQlName: name, name, label: name, labelColumn: 'name',
        primaryKeys: ['id'], isEditable: true, metadata: {}, columns: [],
        multiJoins: [], singleJoins: [], ...opts,
    };
}

const m2m: ManyToManyJoin = {
    name: 'enrollments', targetTable: 'courses', junctionTable: 'enrollments',
    junctionTargetField: 'courses',
    sourceColumnNames: ['id'], junctionSourceColumnNames: ['student_id'],
    junctionTargetColumnNames: ['course_id'], targetColumnNames: ['id'], hasPayload: true,
};

const junction = table('enrollments', {
    label: 'Enrollments',
    columns: [
        col('id', { isPrimaryKey: true, paramType: 'Int' }),
        col('student_id', { paramType: 'Int' }),
        col('course_id', { paramType: 'Int' }),
        col('grade'),
    ],
});
const target = table('courses', { label: 'Courses', labelColumn: 'title' });

const junctionRows = [
    { id: 1, grade: 'A', courses: { id: 10, label: 'Algebra' } },
    { id: 2, grade: 'B', courses: { id: 20, label: 'Biology' } },
];

function renderPanel(onOpenColumn = vi.fn()) {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return render(
        <QueryClientProvider client={client}>
            <M2mPanel parentTable={table('students')} m2m={m2m} parentRowId="42" onOpenColumn={onOpenColumn} />
        </QueryClientProvider>
    );
}

describe('M2mPanel', () => {
    beforeEach(() => {
        vi.clearAllMocks();
        findTable.mockImplementation((n: string) => (n === 'enrollments' ? junction : n === 'courses' ? target : undefined));
        fetcherQuery.mockResolvedValue({ enrollments: { data: junctionRows } });
    });

    it('shows the via-junction indicator and the linked target labels', async () => {
        renderPanel();
        expect(screen.getByText(/via Enrollments/)).toBeInTheDocument();
        expect(await screen.findByText('Algebra')).toBeInTheDocument();
        expect(screen.getByText('Biology')).toBeInTheDocument();
    });

    it('always shows the junction payload columns', async () => {
        renderPanel();
        await screen.findByText('Algebra');
        // Payload header + value are visible without any toggle.
        expect(screen.getByText('grade')).toBeInTheDocument();
        expect(screen.getByText('A')).toBeInTheDocument();
        // No show/hide toggle is rendered.
        expect(screen.queryByText(/Show Enrollments fields/)).not.toBeInTheDocument();
    });

    it('detaches a link by deleting its junction row primary key', async () => {
        renderPanel();
        await screen.findByText('Algebra');
        // Detach now requires confirmation before deleting the junction row.
        fireEvent.click(screen.getByLabelText('Detach Algebra'));
        const confirm = await screen.findByRole('button', { name: 'Detach' });
        fireEvent.click(confirm);
        await waitFor(() => expect(deleteRow).toHaveBeenCalledWith({ id: 1 }));
    });

    it('drills to the target entity, skipping the junction', async () => {
        const onOpenColumn = vi.fn();
        renderPanel(onOpenColumn);
        await screen.findByText('Algebra');
        fireEvent.click(screen.getByLabelText('Open Algebra in side column'));
        expect(onOpenColumn).toHaveBeenCalledWith({ tableName: 'courses', filterId: '10' });
    });

    describe('target picker duplicate guard', () => {
        // The picker plan needs the target's columns (label lookup fails fast
        // on schema drift), so enrich the target for these tests.
        const targetWithColumns = table('courses', {
            label: 'Courses',
            labelColumn: 'title',
            columns: [col('id', { isPrimaryKey: true, paramType: 'Int' }), col('title')],
        });

        beforeEach(() => {
            findTable.mockImplementation((n: string) =>
                n === 'enrollments' ? junction : n === 'courses' ? targetWithColumns : undefined);
            fetcherQuery.mockImplementation(async (q: string) =>
                q.includes('PickTarget')
                    ? { courses: { data: [{ id: 10, label: 'Algebra' }, { id: 30, label: 'Chemistry' }] } }
                    : { enrollments: { data: junctionRows } });
        });

        async function openPicker() {
            renderPanel();
            await screen.findByText('Algebra'); // linked rows loaded
            fireEvent.click(screen.getByRole('button', { name: /Add link/ }));
            await screen.findByRole('button', { name: /Chemistry/ }); // picker list loaded
        }

        it('disables targets that are already linked and marks them', async () => {
            await openPicker();
            // Algebra (id 10) is already linked through junction row 1.
            const linked = screen.getByRole('button', { name: /Algebra/ });
            expect(linked).toBeDisabled();
            expect(screen.getByText('Already linked')).toBeInTheDocument();
            // Chemistry (id 30) is not linked and stays pickable.
            expect(screen.getByRole('button', { name: /Chemistry/ })).toBeEnabled();
        });

        it('inserts a junction row only for an unlinked target', async () => {
            await openPicker();
            fireEvent.click(screen.getByRole('button', { name: /Chemistry/ }));
            await waitFor(() =>
                expect(insert).toHaveBeenCalledWith({ student_id: '42', course_id: '30' }));
            expect(insert).toHaveBeenCalledTimes(1);
        });
    });
});
