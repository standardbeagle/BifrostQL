// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { useHealthCheck } from './useHealthCheck';

/**
 * The server keeps its database binding in memory, so a restart answers
 * /api/health with 200 + connected:false. These tests pin the recovery
 * behaviour that turns that state into a reconnect prompt instead of an
 * editor stuck forever on "Connecting…".
 */
describe('useHealthCheck', () => {
  let fetchMock: ReturnType<typeof vi.fn>;

  const health = (body: unknown, ok = true) =>
    Promise.resolve({ ok, status: ok ? 200 : 500, json: () => Promise.resolve(body) } as Response);

  const render = () => {
    const setErrorMessage = vi.fn();
    const setConnectionState = vi.fn();
    const onServerUnbound = vi.fn();
    const view = renderHook(() =>
      useHealthCheck(setErrorMessage, setConnectionState, onServerUnbound),
    );
    return { setErrorMessage, setConnectionState, onServerUnbound, view };
  };

  beforeEach(() => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('reports the server as unbound after two consecutive connected:false polls', async () => {
    fetchMock.mockImplementation(() => health({ status: 'ok', connected: false }));
    const { onServerUnbound } = render();

    // First poll fires on mount and must NOT eject — a restored vault session
    // is still rebinding the server at that point.
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));
    expect(onServerUnbound).not.toHaveBeenCalled();

    await vi.advanceTimersByTimeAsync(5000);
    await waitFor(() => expect(onServerUnbound).toHaveBeenCalledTimes(1));
  });

  it('does not report unbound when a late bind lands between polls', async () => {
    fetchMock
      .mockImplementationOnce(() => health({ status: 'ok', connected: false }))
      .mockImplementation(() => health({ status: 'ok', connected: true }));
    const { onServerUnbound } = render();

    await vi.advanceTimersByTimeAsync(15000);
    expect(onServerUnbound).not.toHaveBeenCalled();
  });

  it('surfaces the unreachable banner after two failures without reporting unbound', async () => {
    fetchMock.mockImplementation(() => Promise.reject(new Error('offline')));
    const { setErrorMessage, setConnectionState, onServerUnbound } = render();

    await vi.advanceTimersByTimeAsync(5000);
    await waitFor(() =>
      expect(setErrorMessage).toHaveBeenCalledWith(
        'Backend server is not reachable. Waiting for reconnect...',
      ),
    );
    expect(setConnectionState).toHaveBeenCalledWith('error');
    expect(onServerUnbound).not.toHaveBeenCalled();
  });
});
