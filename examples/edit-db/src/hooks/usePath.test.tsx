import { describe, it, expect } from 'vitest';
import { renderHook } from '@testing-library/react';
import { useNavigate, useSearchParams, useNavigation } from './usePath';

/**
 * These hooks sit under every grid. Their return values feed useCallback and
 * useMemo dependency arrays all the way down to `queryVariables`, whose object
 * identity is part of the react-query key — so a fresh identity here re-keys the
 * data query on every render and silently defeats the whole memo chain below it.
 */
describe('usePath hook identity stability', () => {
    it('returns the same navigate function across re-renders', () => {
        const { result, rerender } = renderHook(() => useNavigate());
        const first = result.current;
        rerender();
        expect(result.current).toBe(first);
    });

    it('returns the same search-params object across re-renders', () => {
        const { result, rerender } = renderHook(() => useSearchParams());
        const first = result.current;
        rerender();
        expect(result.current).toBe(first);
        expect(result.current.search).toBe(first.search);
    });

    it('returns the same navigation object across re-renders', () => {
        const { result, rerender } = renderHook(() => useNavigation());
        const first = result.current;
        rerender();
        expect(result.current).toBe(first);
    });
});
