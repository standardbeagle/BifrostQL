import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import '@testing-library/jest-dom';
import type { ColumnDef } from '@tanstack/react-table';
import { DataTable } from './data-table';
import type { PkFilter } from '@/lib/row-id';

// Jsdom shims: DataTable uses `ResizeObserver` for fit-to-height sizing and the Popover API
// for the hover action toolbar. Neither is implemented in jsdom by default.
if (typeof HTMLElement !== 'undefined') {
    const proto = HTMLElement.prototype as unknown as {
        showPopover?: () => void;
        hidePopover?: () => void;
    };
    if (!proto.showPopover) proto.showPopover = () => { /* no-op */ };
    if (!proto.hidePopover) proto.hidePopover = () => { /* no-op */ };
}
class NoopResizeObserver {
    observe() { /* noop */ }
    unobserve() { /* noop */ }
    disconnect() { /* noop */ }
}
(globalThis as unknown as { ResizeObserver: typeof NoopResizeObserver }).ResizeObserver = NoopResizeObserver;

interface EnrollmentRow {
    student_id: number;
    course_id: string;
    grade: string;
    [key: string]: unknown;
}

function makeColumns(): ColumnDef<EnrollmentRow, unknown>[] {
    return [
        {
            id: 'student_id',
            accessorKey: 'student_id',
            header: 'Student',
            cell: ({ row }) => <span>{row.original.student_id}</span>,
        },
        {
            id: 'course_id',
            accessorKey: 'course_id',
            header: 'Course',
            cell: ({ row }) => <span>{row.original.course_id}</span>,
        },
        {
            id: 'grade',
            accessorKey: 'grade',
            header: 'Grade',
            cell: ({ row }) => <span data-testid={`grade-${row.original.student_id}-${row.original.course_id}`}>{row.original.grade}</span>,
        },
    ];
}

// Junction table: student_id=1 appears on two rows; single-column keying would collide.
const enrollmentRows: EnrollmentRow[] = [
    { student_id: 1, course_id: 'cs-101', grade: 'A' },
    { student_id: 1, course_id: 'cs-202', grade: 'B+' },
    { student_id: 2, course_id: 'cs-101', grade: 'A-' },
];

interface RenderOverrides {
    primaryKeys?: string[];
}

function renderDataTable(overrides: RenderOverrides = {}) {
    return render(
        <DataTable<EnrollmentRow>
            columns={makeColumns()}
            data={enrollmentRows}
            pageCount={1}
            pageIndex={0}
            pageSize={50}
            sorting={[]}
            columnFilters={[]}
            primaryKeys={overrides.primaryKeys ?? ['student_id', 'course_id']}
            onSortingChange={() => { /* noop */ }}
            onColumnFiltersChange={() => { /* noop */ }}
            onPageIndexChange={() => { /* noop */ }}
            onPageSizeChange={() => { /* noop */ }}
        />,
    );
}

describe('DataTable composite primary key rendering', () => {
    let errorSpy: ReturnType<typeof vi.spyOn>;

    beforeEach(() => {
        errorSpy = vi.spyOn(console, 'error').mockImplementation(() => { /* capture */ });
    });

    afterEach(() => {
        errorSpy.mockRestore();
    });

    it('renders a junction table with no React duplicate-key warnings', () => {
        renderDataTable();

        const duplicateKeyWarnings = errorSpy.mock.calls.filter((call) =>
            call.some((arg) => typeof arg === 'string' && arg.includes('same key')),
        );
        expect(duplicateKeyWarnings).toEqual([]);
    });

    it('renders every composite-PK row in the fixture', () => {
        renderDataTable();

        expect(screen.getByTestId('grade-1-cs-101')).toHaveTextContent('A');
        expect(screen.getByTestId('grade-1-cs-202')).toHaveTextContent('B+');
        expect(screen.getByTestId('grade-2-cs-101')).toHaveTextContent('A-');
    });

    it('renders rows without crashing when the table has no primary keys (fallback row-${index})', () => {
        renderDataTable({ primaryKeys: [] });

        const duplicateKeyWarnings = errorSpy.mock.calls.filter((call) =>
            call.some((arg) => typeof arg === 'string' && arg.includes('same key')),
        );
        expect(duplicateKeyWarnings).toEqual([]);
        expect(screen.getByTestId('grade-1-cs-101')).toBeInTheDocument();
        expect(screen.getByTestId('grade-1-cs-202')).toBeInTheDocument();
    });
});

describe('DataTable action callback payloads', () => {
    it('dispatches PkFilter[] to onDeleteSelected after selecting all rows via the select-all checkbox', async () => {
        const deleteSpy = vi.fn((_pks: PkFilter[]) => { /* noop */ });
        render(
            <DataTable<EnrollmentRow>
                columns={makeColumns()}
                data={enrollmentRows}
                pageCount={1}
                pageIndex={0}
                pageSize={50}
                sorting={[]}
                columnFilters={[]}
                primaryKeys={['student_id', 'course_id']}
                selectable
                onDeleteSelected={deleteSpy}
                onSortingChange={() => { /* noop */ }}
                onColumnFiltersChange={() => { /* noop */ }}
                onPageIndexChange={() => { /* noop */ }}
                onPageSizeChange={() => { /* noop */ }}
            />,
        );

        // Radix Checkbox renders as role=checkbox (not a native input). Click the header one to select all rows.
        const selectAll = screen.getByRole('checkbox', { name: /select all/i });
        fireEvent.click(selectAll);

        // Delete button appears only when something is selected.
        const deleteButton = await screen.findByRole('button', { name: /delete$/i });
        fireEvent.click(deleteButton);

        expect(deleteSpy).toHaveBeenCalledTimes(1);
        const [filters] = deleteSpy.mock.calls[0];
        expect(filters).toEqual([
            { student_id: 1, course_id: 'cs-101' },
            { student_id: 1, course_id: 'cs-202' },
            { student_id: 2, course_id: 'cs-101' },
        ]);
    });
});
