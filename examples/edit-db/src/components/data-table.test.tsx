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

        const duplicateKeyWarnings = errorSpy.mock.calls.filter((call: unknown[]) =>
            call.some((arg: unknown) => typeof arg === 'string' && arg.includes('same key')),
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

        const duplicateKeyWarnings = errorSpy.mock.calls.filter((call: unknown[]) =>
            call.some((arg: unknown) => typeof arg === 'string' && arg.includes('same key')),
        );
        expect(duplicateKeyWarnings).toEqual([]);
        expect(screen.getByTestId('grade-1-cs-101')).toBeInTheDocument();
        expect(screen.getByTestId('grade-1-cs-202')).toBeInTheDocument();
    });
});

describe('DataTable stacking-mode toggle', () => {
    function renderWithToggle(stackingEnabled: boolean, onToggle = vi.fn()) {
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
                stackingEnabled={stackingEnabled}
                onToggleStacking={onToggle}
                onSortingChange={() => { /* noop */ }}
                onColumnFiltersChange={() => { /* noop */ }}
                onPageIndexChange={() => { /* noop */ }}
                onPageSizeChange={() => { /* noop */ }}
            />,
        );
        return onToggle;
    }

    it('does not render the toggle when onToggleStacking is omitted', () => {
        renderDataTable();
        expect(screen.queryByRole('switch')).not.toBeInTheDocument();
    });

    it('renders the toggle as checked and labelled "Stacked" when stacking is enabled', () => {
        renderWithToggle(true);
        const toggle = screen.getByRole('switch');
        expect(toggle).toHaveAttribute('aria-checked', 'true');
        expect(toggle).toHaveTextContent('Stacked');
    });

    it('renders the toggle as unchecked and labelled "Grid" when stacking is disabled', () => {
        renderWithToggle(false);
        const toggle = screen.getByRole('switch');
        expect(toggle).toHaveAttribute('aria-checked', 'false');
        expect(toggle).toHaveTextContent('Grid');
    });

    it('requests the opposite mode when clicked', () => {
        const onToggle = renderWithToggle(true);
        fireEvent.click(screen.getByRole('switch'));
        expect(onToggle).toHaveBeenCalledWith(false);
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

describe('DataTable column-sizing persistence', () => {
    const STORAGE_KEY = 'bifrost-col-sizes:enrollments';

    function renderWithTableName(tableName = 'enrollments') {
        return render(
            <DataTable<EnrollmentRow>
                columns={makeColumns()}
                data={enrollmentRows}
                tableName={tableName}
                pageCount={1}
                pageIndex={0}
                pageSize={50}
                sorting={[]}
                columnFilters={[]}
                primaryKeys={['student_id', 'course_id']}
                onSortingChange={() => { /* noop */ }}
                onColumnFiltersChange={() => { /* noop */ }}
                onPageIndexChange={() => { /* noop */ }}
                onPageSizeChange={() => { /* noop */ }}
            />,
        );
    }

    /** The header resize handles; double-click triggers auto-size (a sizing change). */
    function resizeHandle(container: HTMLElement): Element {
        const handle = container.querySelector('.cursor-col-resize');
        expect(handle).not.toBeNull();
        return handle!;
    }

    beforeEach(() => {
        localStorage.clear();
        vi.useFakeTimers();
    });

    afterEach(() => {
        vi.useRealTimers();
        localStorage.clear();
    });

    it('does not write back the just-loaded sizing on mount', () => {
        localStorage.setItem(STORAGE_KEY, JSON.stringify({ grade: 120 }));
        const setItemSpy = vi.spyOn(Storage.prototype, 'setItem');

        renderWithTableName();
        vi.advanceTimersByTime(400);

        expect(setItemSpy).not.toHaveBeenCalled();
        setItemSpy.mockRestore();
    });

    it('sanitizes malformed persisted sizing on mount', () => {
        localStorage.setItem(
            STORAGE_KEY,
            JSON.stringify({
                student_id: 12,
                course_id: 900,
                grade: 120,
                ignored: 'wide',
                infinite: Infinity,
                missing: null,
            }),
        );
        const setItemSpy = vi.spyOn(Storage.prototype, 'setItem');

        const { container } = renderWithTableName();
        vi.advanceTimersByTime(400);

        const columns = container.querySelectorAll('col');
        expect(columns[0]).toHaveStyle({ width: '60px' });
        expect(columns[1]).toHaveStyle({ width: '450px' });
        expect(columns[2]).toHaveStyle({ width: '120px' });
        expect(setItemSpy).not.toHaveBeenCalled();
        setItemSpy.mockRestore();
    });

    it('persists a resize after the debounce window', () => {
        const { container } = renderWithTableName();

        fireEvent.doubleClick(resizeHandle(container));
        expect(localStorage.getItem(STORAGE_KEY)).toBeNull();

        vi.advanceTimersByTime(300);
        const stored = JSON.parse(localStorage.getItem(STORAGE_KEY) ?? '{}');
        expect(stored.student_id).toEqual(expect.any(Number));
    });

    it('flushes a pending resize on unmount instead of dropping it', () => {
        const { container, unmount } = renderWithTableName();

        fireEvent.doubleClick(resizeHandle(container));
        // Unmount before the 300ms debounce elapses — the resize must still land.
        unmount();

        const stored = JSON.parse(localStorage.getItem(STORAGE_KEY) ?? '{}');
        expect(stored.student_id).toEqual(expect.any(Number));
    });

    it('flushes a pending resize when the table changes before the debounce elapses', () => {
        const { container, rerender } = renderWithTableName();

        fireEvent.doubleClick(resizeHandle(container));

        rerender(
            <DataTable<EnrollmentRow>
                columns={makeColumns()}
                data={enrollmentRows}
                tableName="other_table"
                pageCount={1}
                pageIndex={0}
                pageSize={50}
                sorting={[]}
                columnFilters={[]}
                primaryKeys={['student_id', 'course_id']}
                onSortingChange={() => { /* noop */ }}
                onColumnFiltersChange={() => { /* noop */ }}
                onPageIndexChange={() => { /* noop */ }}
                onPageSizeChange={() => { /* noop */ }}
            />,
        );

        const stored = JSON.parse(localStorage.getItem(STORAGE_KEY) ?? '{}');
        expect(stored.student_id).toEqual(expect.any(Number));
        // And the new table must not immediately write back its just-loaded sizing.
        vi.advanceTimersByTime(400);
        expect(localStorage.getItem('bifrost-col-sizes:other_table')).toBeNull();
    });
});
