import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { BifrostProvider } from '../../components/bifrost-provider';
import { useTableExpansion } from './use-table-expansion';

function createWrapper() {
  return function Wrapper({ children }: { children: ReactNode }) {
    return (
      <BifrostProvider config={{ endpoint: 'http://localhost:5000/graphql' }}>
        {children}
      </BifrostProvider>
    );
  };
}

const childQuery = {
  table: 'orders',
  fields: ['id', 'total'],
  parentKeyField: 'id',
  childFilterField: 'user_id',
};

describe('useTableExpansion', () => {
  beforeEach(() => {
    vi.stubGlobal('fetch', vi.fn());
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  describe('clearChildCache scoping', () => {
    it('leaves other rows still loading when one row is cleared', async () => {
      // Arrange: both rows fetch, and neither request ever settles.
      vi.mocked(fetch).mockImplementation(() => new Promise(() => {}));
      const { result } = renderHook(
        () => useTableExpansion({ expandable: true, childQuery, rowKey: 'id' }),
        { wrapper: createWrapper() },
      );

      act(() => {
        result.current.expansion.fetchChildData('1', { id: 1 });
      });
      await waitFor(() =>
        expect(result.current.expansion.isChildLoading('1')).toBe(true),
      );
      act(() => {
        result.current.expansion.fetchChildData('2', { id: 2 });
      });
      await waitFor(() =>
        expect(result.current.expansion.isChildLoading('2')).toBe(true),
      );

      // Act: clear only row 1's cache.
      act(() => {
        result.current.expansion.clearChildCache('1');
      });

      // Assert: row 2's in-flight fetch keeps its loading state.
      expect(result.current.expansion.isChildLoading('1')).toBe(false);
      expect(result.current.expansion.isChildLoading('2')).toBe(true);
    });

    it('leaves other rows errors intact when one row is cleared', async () => {
      // Arrange: both rows fail to fetch.
      vi.mocked(fetch).mockRejectedValue(new Error('boom'));
      const { result } = renderHook(
        () => useTableExpansion({ expandable: true, childQuery, rowKey: 'id' }),
        { wrapper: createWrapper() },
      );

      act(() => {
        result.current.expansion.fetchChildData('1', { id: 1 });
      });
      await waitFor(() =>
        expect(result.current.expansion.childError('1')).not.toBeNull(),
      );
      act(() => {
        result.current.expansion.fetchChildData('2', { id: 2 });
      });
      await waitFor(() =>
        expect(result.current.expansion.childError('2')).not.toBeNull(),
      );

      // Act: clear only row 1's cache.
      act(() => {
        result.current.expansion.clearChildCache('1');
      });

      // Assert: row 2 keeps its error so the UI can still report the failure.
      expect(result.current.expansion.childError('1')).toBeNull();
      expect(result.current.expansion.childError('2')).not.toBeNull();
    });

    it('clears loading and error state for every row when called with no row id', async () => {
      // Arrange
      vi.mocked(fetch).mockRejectedValue(new Error('boom'));
      const { result } = renderHook(
        () => useTableExpansion({ expandable: true, childQuery, rowKey: 'id' }),
        { wrapper: createWrapper() },
      );

      act(() => {
        result.current.expansion.fetchChildData('1', { id: 1 });
      });
      await waitFor(() =>
        expect(result.current.expansion.childError('1')).not.toBeNull(),
      );

      // Act
      act(() => {
        result.current.expansion.clearChildCache();
      });

      // Assert
      expect(result.current.expansion.childError('1')).toBeNull();
      expect(result.current.expansion.isChildLoading('1')).toBe(false);
    });
  });
});
