import { describe, it, expect, vi } from 'vitest';
import { renderHook, act, waitFor } from '@testing-library/react';
import { useTableEditing } from './use-table-editing';
import type { ColumnConfig } from '../use-bifrost-table.types';

const columns: ColumnConfig[] = [
  { field: 'id', header: 'ID', readOnly: true },
  { field: 'name', header: 'Name' },
  { field: 'email', header: 'Email' },
];

const data = [
  { id: 1, name: 'Alice', email: 'alice@example.com' },
  { id: 2, name: 'Bob', email: 'bob@example.com' },
];

interface HarnessOverrides {
  autoSave?: boolean;
  onRowUpdate?: (
    row: Record<string, unknown>,
    changes: Record<string, unknown>,
  ) => Promise<void>;
  onBatchSave?: (
    rows: Array<{
      row: Record<string, unknown>;
      changes: Record<string, unknown>;
    }>,
  ) => Promise<void>;
  onSaveError?: (
    error: Error,
    context: {
      rowKey: string;
      row: Record<string, unknown>;
      changes: Record<string, unknown>;
    },
  ) => void;
}

function renderEditing(overrides: HarnessOverrides = {}) {
  return renderHook(() =>
    useTableEditing({
      columns,
      editable: true,
      data,
      rowKey: 'id',
      autoSave: overrides.autoSave ?? false,
      onRowUpdate: overrides.onRowUpdate,
      onBatchSave: overrides.onBatchSave,
      onSaveError: overrides.onSaveError,
      refetch: () => {},
    }),
  );
}

describe('useTableEditing save-failure reporting', () => {
  it('surfaces an auto-save rejection instead of swallowing it', async () => {
    // Arrange: auto-save is on and the write rejects.
    const failure = new Error('write rejected');
    const onSaveError = vi.fn();
    const { result } = renderEditing({
      autoSave: true,
      onRowUpdate: vi.fn().mockRejectedValue(failure),
      onSaveError,
    });

    // Act: edit a cell and commit it.
    act(() => {
      result.current.editing.startEditing('1', 'name');
      result.current.editing.setCellValue('1', 'name', 'Alice Updated');
    });
    await act(async () => {
      await result.current.editing.commitCell();
    });

    // Assert: the error reaches the caller and the row stays dirty for retry.
    await waitFor(() => {
      expect(result.current.editing.getRowSaveError('1')).toBe(failure);
    });
    expect(onSaveError).toHaveBeenCalledWith(failure, {
      rowKey: '1',
      row: expect.objectContaining({ id: 1 }),
      changes: { name: 'Alice Updated' },
    });
    expect(result.current.editing.isCellDirty('1', 'name')).toBe(true);
  });

  it('surfaces a saveRow rejection', async () => {
    // Arrange
    const failure = new Error('update failed');
    const onSaveError = vi.fn();
    const { result } = renderEditing({
      onRowUpdate: vi.fn().mockRejectedValue(failure),
      onSaveError,
    });

    act(() => {
      result.current.editing.setCellValue('1', 'name', 'Alice Updated');
    });

    // Act
    let saved: boolean | undefined;
    await act(async () => {
      saved = await result.current.editing.saveRow('1');
    });

    // Assert
    expect(saved).toBe(false);
    expect(result.current.editing.getRowSaveError('1')).toBe(failure);
    expect(onSaveError).toHaveBeenCalledWith(failure, expect.anything());
  });

  it('reports a missing onRowUpdate as an explicit save error', async () => {
    // Arrange: editing is enabled but no write handler was configured.
    const onSaveError = vi.fn();
    const { result } = renderEditing({ onSaveError });

    act(() => {
      result.current.editing.setCellValue('1', 'name', 'Alice Updated');
    });

    // Act
    await act(async () => {
      await result.current.editing.saveRow('1');
    });

    // Assert: the caller learns why nothing was written.
    expect(result.current.editing.getRowSaveError('1')).toBeInstanceOf(Error);
    expect(result.current.editing.getRowSaveError('1')?.message).toMatch(
      /onRowUpdate/,
    );
    expect(onSaveError).toHaveBeenCalled();
  });

  it('surfaces a batch-save rejection on every dirty row', async () => {
    // Arrange
    const failure = new Error('batch failed');
    const onSaveError = vi.fn();
    const { result } = renderEditing({
      onBatchSave: vi.fn().mockRejectedValue(failure),
      onSaveError,
    });

    act(() => {
      result.current.editing.setCellValue('1', 'name', 'Alice Updated');
      result.current.editing.setCellValue('2', 'name', 'Bob Updated');
    });

    // Act
    let outcome: { saved: number; failed: number } | undefined;
    await act(async () => {
      outcome = await result.current.editing.saveAllDirty();
    });

    // Assert
    expect(outcome).toEqual({ saved: 0, failed: 2 });
    expect(result.current.editing.getRowSaveError('1')).toBe(failure);
    expect(result.current.editing.getRowSaveError('2')).toBe(failure);
    expect(onSaveError).toHaveBeenCalledTimes(2);
  });

  it('clears a previous save error once the row saves successfully', async () => {
    // Arrange: first write rejects, second resolves.
    const onRowUpdate = vi
      .fn()
      .mockRejectedValueOnce(new Error('transient'))
      .mockResolvedValueOnce(undefined);
    const { result } = renderEditing({ onRowUpdate });

    act(() => {
      result.current.editing.setCellValue('1', 'name', 'Alice Updated');
    });
    await act(async () => {
      await result.current.editing.saveRow('1');
    });
    expect(result.current.editing.getRowSaveError('1')).not.toBeNull();

    // Act: retry.
    await act(async () => {
      await result.current.editing.saveRow('1');
    });

    // Assert
    expect(result.current.editing.getRowSaveError('1')).toBeNull();
    expect(result.current.editing.isRowDirty('1')).toBe(false);
  });
});
