import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useTableResponsive } from './use-table-responsive';
import type { ColumnConfig } from '../use-bifrost-table.types';

const columns: ColumnConfig[] = [
  { field: 'id', header: 'ID' },
  { field: 'name', header: 'Name' },
];

const visibleColumns = ['id', 'name'];

const data = [
  { id: 1, name: 'Alice' },
  { id: 2, name: 'Bob' },
];

/** Drive requestAnimationFrame synchronously so resize batching is testable. */
function stubAnimationFrame() {
  const pending: FrameRequestCallback[] = [];
  vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => {
    pending.push(cb);
    return pending.length;
  });
  vi.stubGlobal('cancelAnimationFrame', () => {});
  return {
    flush: () => {
      const queued = pending.splice(0, pending.length);
      for (const cb of queued) cb(0);
    },
    get queued() {
      return pending.length;
    },
  };
}

function setWidth(width: number) {
  Object.defineProperty(window, 'innerWidth', {
    value: width,
    configurable: true,
    writable: true,
  });
}

describe('useTableResponsive resize handling', () => {
  let frames: ReturnType<typeof stubAnimationFrame>;

  beforeEach(() => {
    frames = stubAnimationFrame();
    setWidth(1200);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('coalesces a burst of resize events into a single frame', () => {
    // Arrange
    renderHook(() =>
      useTableResponsive({
        breakpointsProp: undefined,
        responsiveColumns: undefined,
        visibleColumns,
        columns,
        rowKey: 'id',
        data,
      }),
    );

    // Act: a scroll-bar drag fires dozens of resize events per frame.
    act(() => {
      for (let i = 0; i < 20; i++) {
        setWidth(1200 - i);
        window.dispatchEvent(new Event('resize'));
      }
    });

    // Assert: one queued frame, not twenty synchronous state updates.
    expect(frames.queued).toBe(1);
  });

  it('does not recompute card view data when the breakpoint bucket is unchanged', () => {
    // Arrange
    const { result } = renderHook(() =>
      useTableResponsive({
        breakpointsProp: undefined,
        responsiveColumns: undefined,
        visibleColumns,
        columns,
        rowKey: 'id',
        data,
      }),
    );
    const before = result.current.cardViewData;
    expect(result.current.currentBreakpoint).toBe('lg');

    // Act: resize within the same bucket (1024..1279 is all 'lg').
    act(() => {
      setWidth(1100);
      window.dispatchEvent(new Event('resize'));
      frames.flush();
    });

    // Assert: the O(rows x cols) projection was not rebuilt.
    expect(result.current.currentBreakpoint).toBe('lg');
    expect(result.current.cardViewData).toBe(before);
  });

  it('recomputes when the resize crosses a breakpoint boundary', () => {
    // Arrange
    const { result } = renderHook(() =>
      useTableResponsive({
        breakpointsProp: undefined,
        responsiveColumns: undefined,
        visibleColumns,
        columns,
        rowKey: 'id',
        data,
      }),
    );
    expect(result.current.currentBreakpoint).toBe('lg');

    // Act
    act(() => {
      setWidth(500);
      window.dispatchEvent(new Event('resize'));
      frames.flush();
    });

    // Assert
    expect(result.current.currentBreakpoint).toBe('xs');
    expect(result.current.isMobile).toBe(true);
  });

  it('honors custom breakpoint thresholds passed as an inline literal', () => {
    // Arrange: the common call-site shape — a fresh object every render.
    setWidth(720);
    const { result, rerender } = renderHook(() =>
      useTableResponsive({
        breakpointsProp: { md: 700 },
        responsiveColumns: undefined,
        visibleColumns,
        columns,
        rowKey: 'id',
        data,
      }),
    );
    expect(result.current.currentBreakpoint).toBe('md');

    // Act / Assert: the resolved thresholds are stable across renders, so the
    // memo is keyed on the numeric values rather than the prop's identity.
    rerender();
    expect(result.current.currentBreakpoint).toBe('md');
  });
});
