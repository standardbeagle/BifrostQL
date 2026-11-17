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
  writable?: (field: string) => boolean;
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
      writable: overrides.writable ?? (() => true),
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

describe('useTableEditing writable gate', () => {
  it('a column the policy will not write is not editable, whatever the table says', () => {
    // Arrange: the table is editable and `name` is not read-only, but the
    // server projected it as not writable.
    const { result } = renderEditing({ writable: (field) => field !== 'name' });

    // Assert
    expect(result.current.editing.isColumnEditable('name')).toBe(false);
    expect(result.current.editing.isColumnEditable('email')).toBe(true);
  });
});

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

/** A write the test resolves by hand, so edits can land while it is in flight. */
function deferredWrite() {
  let resolve!: () => void;
  const promise = new Promise<void>((r) => {
    resolve = r;
  });
  return { promise, resolve };
}

describe('useTableEditing edits made while a save is in flight', () => {
  it('saveRow keeps a field edited during the save', async () => {
    // Arrange: the write hangs until we release it.
    const write = deferredWrite();
    const onRowUpdate = vi.fn().mockReturnValue(write.promise);
    const { result } = renderEditing({ onRowUpdate });
    act(() => {
      result.current.editing.setCellValue('1', 'name', 'Alice Sent');
    });

    // Act: start the save, edit another field and re-edit the sent field
    // while it is in flight, then let the write succeed.
    let saving!: Promise<boolean>;
    act(() => {
      saving = result.current.editing.saveRow('1');
    });
    act(() => {
      result.current.editing.setCellValue('1', 'email', 'new@example.com');
    });
    await act(async () => {
      write.resolve();
      await saving;
    });

    // Assert: only the value that was sent is settled; the newer edit stays.
    expect(onRowUpdate).toHaveBeenCalledWith(
      expect.objectContaining({ id: 1 }),
      { name: 'Alice Sent' },
    );
    expect(result.current.editing.getRowChanges('1')).toEqual({
      email: 'new@example.com',
    });
    expect(result.current.editing.dirtyRows.get('1')?.saving).toBe(false);
  });

  it('saveRow keeps a newer value typed into the field that was being saved', async () => {
    // Arrange
    const write = deferredWrite();
    const { result } = renderEditing({
      onRowUpdate: vi.fn().mockReturnValue(write.promise),
    });
    act(() => {
      result.current.editing.setCellValue('1', 'name', 'Alice Sent');
    });

    // Act
    let saving!: Promise<boolean>;
    act(() => {
      saving = result.current.editing.saveRow('1');
    });
    act(() => {
      result.current.editing.setCellValue('1', 'name', 'Alice Newer');
    });
    await act(async () => {
      write.resolve();
      await saving;
    });

    // Assert
    expect(result.current.editing.getRowChanges('1')).toEqual({
      name: 'Alice Newer',
    });
  });

  it('auto-save keeps a newer value typed into the field that was being saved', async () => {
    // Arrange
    const write = deferredWrite();
    const { result } = renderEditing({
      autoSave: true,
      onRowUpdate: vi.fn().mockReturnValue(write.promise),
    });
    act(() => {
      result.current.editing.startEditing('1', 'name');
      result.current.editing.setCellValue('1', 'name', 'Alice Sent');
    });

    // Act
    let committing!: Promise<void>;
    act(() => {
      committing = result.current.editing.commitCell();
    });
    await act(async () => {
      await Promise.resolve();
    });
    act(() => {
      result.current.editing.setCellValue('1', 'name', 'Alice Newer');
    });
    await act(async () => {
      write.resolve();
      await committing;
    });

    // Assert
    expect(result.current.editing.getRowChanges('1')).toEqual({
      name: 'Alice Newer',
    });
  });

  it('batch save keeps edits made while the batch was in flight', async () => {
    // Arrange
    const write = deferredWrite();
    const { result } = renderEditing({
      onBatchSave: vi.fn().mockReturnValue(write.promise),
    });
    act(() => {
      result.current.editing.setCellValue('1', 'name', 'Alice Sent');
    });

    // Act
    let saving!: Promise<{ saved: number; failed: number }>;
    act(() => {
      saving = result.current.editing.saveAllDirty();
    });
    await act(async () => {
      await Promise.resolve();
    });
    act(() => {
      result.current.editing.setCellValue('2', 'name', 'Bob Newer');
    });
    await act(async () => {
      write.resolve();
      await saving;
    });

    // Assert: row 1 settled, row 2's in-flight edit survives.
    expect(result.current.editing.isRowDirty('1')).toBe(false);
    expect(result.current.editing.getRowChanges('2')).toEqual({
      name: 'Bob Newer',
    });
  });
});
