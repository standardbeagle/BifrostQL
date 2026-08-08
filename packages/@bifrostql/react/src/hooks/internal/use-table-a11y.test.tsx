import { describe, it, expect, vi, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useTableA11y } from './use-table-a11y';
import type { ColumnConfig } from '../use-bifrost-table.types';

const columns: ColumnConfig[] = [
  { field: 'id', header: 'ID' },
  { field: 'name', header: 'Name' },
  { field: 'email', header: 'Email' },
];

const visibleColumns = ['id', 'name', 'email'];

const data = [
  { id: 1, name: 'Alice', email: 'alice@example.com' },
  { id: 2, name: 'Bob', email: 'bob@example.com' },
  { id: 3, name: 'Carol', email: 'carol@example.com' },
];

function renderA11y(
  overrides: Partial<{
    editingCell: null;
    selectedRows: Array<Record<string, unknown>>;
  }> = {},
) {
  return renderHook(() =>
    useTableA11y({
      sort: [],
      columns,
      activeFilterCount: 0,
      data,
      visibleColumns,
      editableColumnSet: new Set(['name', 'email']),
      rowKey: 'id',
      selectedRows: [],
      expandedRows: new Set<string>(),
      editingCell: null,
      tableLabel: undefined,
      table: 'users',
      startEditing: vi.fn(),
      cancelEditing: vi.fn(),
      toggleRow: vi.fn(),
      ...overrides,
    }),
  );
}

/** Count how many of the rendered grid cells claim tabIndex 0. */
function countCellTabStops(
  getCellProps: (rowIndex: number, colIndex: number, field?: string) => {
    tabIndex?: number;
  },
): number {
  let count = 0;
  for (let r = 0; r < data.length; r++) {
    for (let c = 0; c < visibleColumns.length; c++) {
      if (getCellProps(r, c, visibleColumns[c]).tabIndex === 0) count++;
    }
  }
  return count;
}

describe('useTableA11y roving tabindex and DOM focus', () => {
  afterEach(() => {
    document.body.innerHTML = '';
  });

  it('gives exactly one cell a tab stop, matching both row and column', () => {
    // Arrange
    const { result } = renderA11y();

    // Act: focus row 1, column 2.
    act(() => {
      result.current.keyboard.setFocusedCell({ rowIndex: 1, colIndex: 2 });
    });

    // Assert: a single tab stop, at the focused coordinates — not one per row.
    expect(countCellTabStops(result.current.getCellProps)).toBe(1);
    expect(result.current.getCellProps(1, 2, 'email').tabIndex).toBe(0);
    expect(result.current.getCellProps(0, 2, 'email').tabIndex).toBe(-1);
    expect(result.current.getCellProps(1, 0, 'id').tabIndex).toBe(-1);
  });

  it('moves DOM focus to the registered cell when the focused cell changes', () => {
    // Arrange: two real cells registered with the hook.
    const { result } = renderA11y();
    const cellA = document.createElement('td');
    const cellB = document.createElement('td');
    cellA.tabIndex = -1;
    cellB.tabIndex = -1;
    document.body.append(cellA, cellB);

    act(() => {
      result.current.keyboard.registerCell(0, 0, cellA);
      result.current.keyboard.registerCell(2, 1, cellB);
    });

    // Act
    act(() => {
      result.current.keyboard.setFocusedCell({ rowIndex: 0, colIndex: 0 });
    });

    // Assert: focus actually moved, so a screen reader announces the cell.
    expect(document.activeElement).toBe(cellA);

    // Act: arrow-key navigation must move DOM focus too.
    act(() => {
      result.current.keyboard.setFocusedCell({ rowIndex: 2, colIndex: 1 });
    });

    // Assert
    expect(document.activeElement).toBe(cellB);
  });

  it('exposes a ref from getCellProps that registers the cell for focus', () => {
    // Arrange
    const { result } = renderA11y();
    const cell = document.createElement('td');
    cell.tabIndex = -1;
    document.body.appendChild(cell);

    // Act: wire the cell the way a consumer spreading the props would.
    act(() => {
      result.current.getCellProps(1, 1, 'name').ref?.(cell);
    });
    act(() => {
      result.current.keyboard.setFocusedCell({ rowIndex: 1, colIndex: 1 });
    });

    // Assert
    expect(document.activeElement).toBe(cell);
  });

  it('leaves exactly one tab stop after Escape rather than stranding focus', () => {
    // Arrange
    const { result } = renderA11y();
    act(() => {
      result.current.keyboard.setFocusedCell({ rowIndex: 2, colIndex: 2 });
    });

    // Act
    act(() => {
      result.current.keyboard.handleKeyDown({
        key: 'Escape',
        preventDefault: vi.fn(),
      });
    });

    // Assert: the grid still holds a reachable tab stop.
    const headerStops = visibleColumns.filter(
      (field, c) => result.current.getHeaderCellProps(c, field).tabIndex === 0,
    ).length;
    expect(headerStops + countCellTabStops(result.current.getCellProps)).toBe(
      1,
    );
  });

  it('reports selection per row from the indexed key sets', () => {
    // Arrange: row 2 is selected.
    const { result } = renderA11y({ selectedRows: [data[1]] });

    // Act / Assert: aria-selected tracks the selection for known rows only.
    expect(result.current.getRowProps(0, '1')['aria-selected']).toBe(false);
    expect(result.current.getRowProps(1, '2')['aria-selected']).toBe(true);
    expect(result.current.getRowProps(2, '3')['aria-selected']).toBe(false);
    // A key that is not in the current page has no selection state to report.
    expect(result.current.getRowProps(0, '999')['aria-selected']).toBeUndefined();
    expect(result.current.getRowProps(0)['aria-selected']).toBeUndefined();
  });

  it('moves DOM focus with arrow keys', () => {
    // Arrange
    const { result } = renderA11y();
    const target = document.createElement('td');
    target.tabIndex = -1;
    document.body.appendChild(target);
    act(() => {
      result.current.keyboard.registerCell(1, 0, target);
      result.current.keyboard.setFocusedCell({ rowIndex: 0, colIndex: 0 });
    });

    // Act
    act(() => {
      result.current.keyboard.handleKeyDown({
        key: 'ArrowDown',
        preventDefault: vi.fn(),
      });
    });

    // Assert
    expect(result.current.keyboard.focusedCell).toEqual({
      rowIndex: 1,
      colIndex: 0,
    });
    expect(document.activeElement).toBe(target);
  });
});
