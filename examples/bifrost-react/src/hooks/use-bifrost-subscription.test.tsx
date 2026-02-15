import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { BifrostProvider } from '../components/bifrost-provider';
import { useBifrostSubscription } from './use-bifrost-subscription';

type WsHandler = {
  onopen: (() => void) | null;
  onmessage: ((event: { data: string }) => void) | null;
  onerror: (() => void) | null;
  onclose: (() => void) | null;
};

function createMockWebSocket() {
  const instances: Array<{
    url: string;
    protocol: string;
    handler: WsHandler;
    sent: string[];
    readyState: number;
    close: ReturnType<typeof vi.fn>;
  }> = [];

  const MockWebSocket = vi
    .fn()
    .mockImplementation((url: string, protocol: string) => {
      const handler: WsHandler = {
        onopen: null,
        onmessage: null,
        onerror: null,
        onclose: null,
      };

      const instance = {
        url,
        protocol,
        handler,
        sent: [] as string[],
        readyState: 1,
        close: vi.fn(),
        send: vi.fn((data: string) => {
          instance.sent.push(data);
        }),
        set onopen(fn: (() => void) | null) {
          handler.onopen = fn;
        },
        get onopen() {
          return handler.onopen;
        },
        set onmessage(fn: ((event: { data: string }) => void) | null) {
          handler.onmessage = fn;
        },
        get onmessage() {
          return handler.onmessage;
        },
        set onerror(fn: (() => void) | null) {
          handler.onerror = fn;
        },
        get onerror() {
          return handler.onerror;
        },
        set onclose(fn: (() => void) | null) {
          handler.onclose = fn;
        },
        get onclose() {
          return handler.onclose;
        },
      };

      instances.push(instance);
      return instance;
    });

  (MockWebSocket as unknown as Record<string, number>).OPEN = 1;
  (MockWebSocket as unknown as Record<string, number>).CLOSED = 3;

  return { MockWebSocket, instances };
}

type SseHandler = {
  onopen: (() => void) | null;
  onmessage: ((event: { data: string }) => void) | null;
  onerror: (() => void) | null;
};

function createMockEventSource() {
  const instances: Array<{
    url: string;
    handler: SseHandler;
    readyState: number;
    close: ReturnType<typeof vi.fn>;
  }> = [];

  const MockEventSource = vi.fn().mockImplementation((url: string) => {
    const handler: SseHandler = {
      onopen: null,
      onmessage: null,
      onerror: null,
    };

    const instance = {
      url,
      handler,
      readyState: 0,
      close: vi.fn(),
      set onopen(fn: (() => void) | null) {
        handler.onopen = fn;
      },
      get onopen() {
        return handler.onopen;
      },
      set onmessage(fn: ((event: { data: string }) => void) | null) {
        handler.onmessage = fn;
      },
      get onmessage() {
        return handler.onmessage;
      },
      set onerror(fn: (() => void) | null) {
        handler.onerror = fn;
      },
      get onerror() {
        return handler.onerror;
      },
    };

    instances.push(instance);
    return instance;
  });

  (MockEventSource as unknown as Record<string, number>).CLOSED = 2;
  (MockEventSource as unknown as Record<string, number>).CONNECTING = 0;
  (MockEventSource as unknown as Record<string, number>).OPEN = 1;

  return { MockEventSource, instances };
}

function createWrapper(
  endpoint = 'http://localhost:5000/graphql',
  headers?: Record<string, string>,
) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
    },
  });

  return function Wrapper({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={queryClient}>
        <BifrostProvider config={{ endpoint, headers }}>
          {children}
        </BifrostProvider>
      </QueryClientProvider>
    );
  };
}

describe('useBifrostSubscription', () => {
  let originalWebSocket: typeof globalThis.WebSocket;
  let originalEventSource: typeof globalThis.EventSource;

  beforeEach(() => {
    originalWebSocket = globalThis.WebSocket;
    originalEventSource = globalThis.EventSource;
  });

  afterEach(() => {
    globalThis.WebSocket = originalWebSocket;
    globalThis.EventSource = originalEventSource;
    vi.restoreAllMocks();
  });

  it('throws when used outside BifrostProvider', () => {
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });

    function Wrapper({ children }: { children: ReactNode }) {
      return (
        <QueryClientProvider client={queryClient}>
          {children}
        </QueryClientProvider>
      );
    }

    expect(() => {
      renderHook(
        () =>
          useBifrostSubscription({
            subscription: 'subscription { orderUpdated { id } }',
          }),
        { wrapper: Wrapper },
      );
    }).toThrow('useBifrostSubscription must be used within a BifrostProvider');
  });

  it('starts in disconnected state when not enabled', () => {
    const { MockWebSocket } = createMockWebSocket();
    globalThis.WebSocket = MockWebSocket as unknown as typeof WebSocket;

    const { result } = renderHook(
      () =>
        useBifrostSubscription({
          subscription: 'subscription { orderUpdated { id } }',
          enabled: false,
        }),
      { wrapper: createWrapper() },
    );

    expect(result.current.connectionState).toBe('disconnected');
    expect(result.current.isConnected).toBe(false);
    expect(result.current.data).toBeUndefined();
    expect(result.current.error).toBeUndefined();
    expect(MockWebSocket).not.toHaveBeenCalled();
  });

  describe('WebSocket transport', () => {
    it('connects using graphql-transport-ws protocol', () => {
      const { MockWebSocket, instances } = createMockWebSocket();
      globalThis.WebSocket = MockWebSocket as unknown as typeof WebSocket;

      renderHook(
        () =>
          useBifrostSubscription({
            subscription: 'subscription { orderUpdated { id } }',
            transport: 'websocket',
          }),
        { wrapper: createWrapper() },
      );

      expect(instances).toHaveLength(1);
      expect(instances[0].url).toBe('ws://localhost:5000/graphql');
      expect(instances[0].protocol).toBe('graphql-transport-ws');
    });

    it('converts https endpoint to wss', () => {
      const { MockWebSocket, instances } = createMockWebSocket();
      globalThis.WebSocket = MockWebSocket as unknown as typeof WebSocket;

      renderHook(
        () =>
          useBifrostSubscription({
            subscription: 'subscription { orderUpdated { id } }',
            transport: 'websocket',
          }),
        { wrapper: createWrapper('https://api.example.com/graphql') },
      );

      expect(instances[0].url).toBe('wss://api.example.com/graphql');
    });

    it('transitions to connecting state immediately', () => {
      const { MockWebSocket } = createMockWebSocket();
      globalThis.WebSocket = MockWebSocket as unknown as typeof WebSocket;

      const { result } = renderHook(
        () =>
          useBifrostSubscription({
            subscription: 'subscription { orderUpdated { id } }',
            transport: 'websocket',
          }),
        { wrapper: createWrapper() },
      );

      expect(result.current.connectionState).toBe('connecting');
    });

    it('sends connection_init on open and subscribes on ack', async () => {
      const { MockWebSocket, instances } = createMockWebSocket();
      globalThis.WebSocket = MockWebSocket as unknown as typeof WebSocket;

      renderHook(
        () =>
          useBifrostSubscription({
            subscription: 'subscription { orderUpdated { id status } }',
            variables: { userId: 123 },
            transport: 'websocket',
          }),
        { wrapper: createWrapper() },
      );

      const ws = instances[0];

      await act(async () => {
        ws.handler.onopen?.();
      });

      expect(ws.sent).toHaveLength(1);
      expect(JSON.parse(ws.sent[0])).toEqual({
        type: 'connection_init',
      });

      await act(async () => {
        ws.handler.onmessage?.({
          data: JSON.stringify({ type: 'connection_ack' }),
        });
      });

      expect(ws.sent).toHaveLength(2);
      expect(JSON.parse(ws.sent[1])).toEqual({
        type: 'subscribe',
        id: '1',
        payload: {
          query: 'subscription { orderUpdated { id status } }',
          variables: { userId: 123 },
        },
      });
    });

    it('sends headers in connection_init payload', async () => {
      const { MockWebSocket, instances } = createMockWebSocket();
      globalThis.WebSocket = MockWebSocket as unknown as typeof WebSocket;

      renderHook(
        () =>
          useBifrostSubscription({
            subscription: 'subscription { orderUpdated { id } }',
            transport: 'websocket',
          }),
        {
          wrapper: createWrapper('http://localhost:5000/graphql', {
            Authorization: 'Bearer test-token',
          }),
        },
      );

      const ws = instances[0];

      await act(async () => {
        ws.handler.onopen?.();
      });

      expect(JSON.parse(ws.sent[0])).toEqual({
        type: 'connection_init',
        payload: { Authorization: 'Bearer test-token' },
      });
    });

    it('transitions to connected state on ack', async () => {
      const { MockWebSocket, instances } = createMockWebSocket();
      globalThis.WebSocket = MockWebSocket as unknown as typeof WebSocket;

      const { result } = renderHook(
        () =>
          useBifrostSubscription({
            subscription: 'subscription { orderUpdated { id } }',
            transport: 'websocket',
          }),
        { wrapper: createWrapper() },
      );

      const ws = instances[0];

      await act(async () => {
        ws.handler.onopen?.();
      });

      await act(async () => {
        ws.handler.onmessage?.({
          data: JSON.stringify({ type: 'connection_ack' }),
        });
      });

      expect(result.current.connectionState).toBe('connected');
      expect(result.current.isConnected).toBe(true);
    });

    it('receives data from next messages', async () => {
      const { MockWebSocket, instances } = createMockWebSocket();
      globalThis.WebSocket = MockWebSocket as unknown as typeof WebSocket;

      const onData = vi.fn();

      const { result } = renderHook(
        () =>
          useBifrostSubscription<{ orderUpdated: { id: number } }>({
            subscription: 'subscription { orderUpdated { id } }',
            transport: 'websocket',
            onData,
          }),
        { wrapper: createWrapper() },
      );

      const ws = instances[0];

      await act(async () => {
        ws.handler.onopen?.();
        ws.handler.onmessage?.({
          data: JSON.stringify({ type: 'connection_ack' }),
        });
      });

      await act(async () => {
        ws.handler.onmessage?.({
          data: JSON.stringify({
            type: 'next',
            id: '1',
            payload: { data: { orderUpdated: { id: 42 } } },
          }),
        });
      });

      expect(result.current.data).toEqual({ orderUpdated: { id: 42 } });
      expect(onData).toHaveBeenCalledWith({ orderUpdated: { id: 42 } });
    });

    it('handles subscription errors', async () => {
      const { MockWebSocket, instances } = createMockWebSocket();
      globalThis.WebSocket = MockWebSocket as unknown as typeof WebSocket;

      const onError = vi.fn();

      const { result } = renderHook(
        () =>
          useBifrostSubscription({
            subscription: 'subscription { orderUpdated { id } }',
            transport: 'websocket',
            onError,
            reconnectAttempts: 0,
          }),
        { wrapper: createWrapper() },
      );

      const ws = instances[0];

      await act(async () => {
        ws.handler.onopen?.();
        ws.handler.onmessage?.({
          data: JSON.stringify({ type: 'connection_ack' }),
        });
      });

      await act(async () => {
        ws.handler.onmessage?.({
          data: JSON.stringify({
            type: 'error',
            id: '1',
            payload: [{ message: 'Permission denied' }],
          }),
        });
      });

      expect(result.current.error?.message).toBe('Permission denied');
      expect(onError).toHaveBeenCalledTimes(1);
    });

    it('responds to ping with pong', async () => {
      const { MockWebSocket, instances } = createMockWebSocket();
      globalThis.WebSocket = MockWebSocket as unknown as typeof WebSocket;

      renderHook(
        () =>
          useBifrostSubscription({
            subscription: 'subscription { orderUpdated { id } }',
            transport: 'websocket',
          }),
        { wrapper: createWrapper() },
      );

      const ws = instances[0];

      await act(async () => {
        ws.handler.onopen?.();
        ws.handler.onmessage?.({
          data: JSON.stringify({ type: 'connection_ack' }),
        });
      });

      await act(async () => {
        ws.handler.onmessage?.({
          data: JSON.stringify({ type: 'ping' }),
        });
      });

      const pongMessage = ws.sent.find(
        (msg) => JSON.parse(msg).type === 'pong',
      );
      expect(pongMessage).toBeDefined();
      expect(JSON.parse(pongMessage!)).toEqual({ type: 'pong' });
    });

    it('transitions to error on connection failure', async () => {
      const { MockWebSocket, instances } = createMockWebSocket();
      globalThis.WebSocket = MockWebSocket as unknown as typeof WebSocket;

      const { result } = renderHook(
        () =>
          useBifrostSubscription({
            subscription: 'subscription { orderUpdated { id } }',
            transport: 'websocket',
            reconnectAttempts: 0,
          }),
        { wrapper: createWrapper() },
      );

      const ws = instances[0];

      await act(async () => {
        ws.handler.onerror?.();
      });

      expect(result.current.connectionState).toBe('error');
      expect(result.current.error?.message).toBe('WebSocket connection failed');
    });

    it('transitions to disconnected on close after being connected', async () => {
      const { MockWebSocket, instances } = createMockWebSocket();
      globalThis.WebSocket = MockWebSocket as unknown as typeof WebSocket;

      const { result } = renderHook(
        () =>
          useBifrostSubscription({
            subscription: 'subscription { orderUpdated { id } }',
            transport: 'websocket',
            reconnectAttempts: 0,
          }),
        { wrapper: createWrapper() },
      );

      const ws = instances[0];

      await act(async () => {
        ws.handler.onopen?.();
        ws.handler.onmessage?.({
          data: JSON.stringify({ type: 'connection_ack' }),
        });
      });

      expect(result.current.isConnected).toBe(true);

      await act(async () => {
        ws.handler.onclose?.();
      });

      expect(result.current.connectionState).toBe('disconnected');
      expect(result.current.isConnected).toBe(false);
    });

    it('sends complete message on close when connected', () => {
      const { MockWebSocket, instances } = createMockWebSocket();
      globalThis.WebSocket = MockWebSocket as unknown as typeof WebSocket;

      const { unmount } = renderHook(
        () =>
          useBifrostSubscription({
            subscription: 'subscription { orderUpdated { id } }',
            transport: 'websocket',
          }),
        { wrapper: createWrapper() },
      );

      const ws = instances[0];
      ws.readyState = 1;

      unmount();

      const completeMsg = ws.sent.find(
        (msg) => JSON.parse(msg).type === 'complete',
      );
      expect(completeMsg).toBeDefined();
      expect(ws.close).toHaveBeenCalled();
    });

    it('clears data error on new data', async () => {
      const { MockWebSocket, instances } = createMockWebSocket();
      globalThis.WebSocket = MockWebSocket as unknown as typeof WebSocket;

      const { result } = renderHook(
        () =>
          useBifrostSubscription<{ orderUpdated: { id: number } }>({
            subscription: 'subscription { orderUpdated { id } }',
            transport: 'websocket',
            reconnectAttempts: 0,
          }),
        { wrapper: createWrapper() },
      );

      const ws = instances[0];

      await act(async () => {
        ws.handler.onopen?.();
        ws.handler.onmessage?.({
          data: JSON.stringify({ type: 'connection_ack' }),
        });
      });

      await act(async () => {
        ws.handler.onmessage?.({
          data: JSON.stringify({
            type: 'error',
            id: '1',
            payload: [{ message: 'Temporary error' }],
          }),
        });
      });

      expect(result.current.error).toBeDefined();

      await act(async () => {
        ws.handler.onmessage?.({
          data: JSON.stringify({
            type: 'next',
            id: '1',
            payload: { data: { orderUpdated: { id: 1 } } },
          }),
        });
      });

      expect(result.current.error).toBeUndefined();
      expect(result.current.data).toEqual({ orderUpdated: { id: 1 } });
    });
  });

  describe('SSE transport', () => {
    it('connects to endpoint with query params', () => {
      const { MockEventSource, instances } = createMockEventSource();
      globalThis.EventSource = MockEventSource as unknown as typeof EventSource;

      renderHook(
        () =>
          useBifrostSubscription({
            subscription: 'subscription { orderUpdated { id } }',
            transport: 'sse',
          }),
        { wrapper: createWrapper() },
      );

      expect(instances).toHaveLength(1);
      expect(instances[0].url).toContain('http://localhost:5000/graphql?');
      expect(instances[0].url).toContain(
        'query=subscription+%7B+orderUpdated+%7B+id+%7D+%7D',
      );
    });

    it('includes variables as query parameter', () => {
      const { MockEventSource, instances } = createMockEventSource();
      globalThis.EventSource = MockEventSource as unknown as typeof EventSource;

      renderHook(
        () =>
          useBifrostSubscription({
            subscription: 'subscription { orderUpdated { id } }',
            variables: { userId: 123 },
            transport: 'sse',
          }),
        { wrapper: createWrapper() },
      );

      const url = new URL(instances[0].url);
      expect(url.searchParams.get('variables')).toBe(
        JSON.stringify({ userId: 123 }),
      );
    });

    it('transitions to connected on open', async () => {
      const { MockEventSource, instances } = createMockEventSource();
      globalThis.EventSource = MockEventSource as unknown as typeof EventSource;

      const { result } = renderHook(
        () =>
          useBifrostSubscription({
            subscription: 'subscription { orderUpdated { id } }',
            transport: 'sse',
          }),
        { wrapper: createWrapper() },
      );

      expect(result.current.connectionState).toBe('connecting');

      await act(async () => {
        instances[0].handler.onopen?.();
      });

      expect(result.current.connectionState).toBe('connected');
      expect(result.current.isConnected).toBe(true);
    });

    it('receives data from SSE messages', async () => {
      const { MockEventSource, instances } = createMockEventSource();
      globalThis.EventSource = MockEventSource as unknown as typeof EventSource;

      const onData = vi.fn();

      const { result } = renderHook(
        () =>
          useBifrostSubscription<{ orderUpdated: { id: number } }>({
            subscription: 'subscription { orderUpdated { id } }',
            transport: 'sse',
            onData,
          }),
        { wrapper: createWrapper() },
      );

      await act(async () => {
        instances[0].handler.onopen?.();
      });

      await act(async () => {
        instances[0].handler.onmessage?.({
          data: JSON.stringify({ data: { orderUpdated: { id: 99 } } }),
        });
      });

      expect(result.current.data).toEqual({ orderUpdated: { id: 99 } });
      expect(onData).toHaveBeenCalledWith({ orderUpdated: { id: 99 } });
    });

    it('handles GraphQL errors in SSE messages', async () => {
      const { MockEventSource, instances } = createMockEventSource();
      globalThis.EventSource = MockEventSource as unknown as typeof EventSource;

      const { result } = renderHook(
        () =>
          useBifrostSubscription({
            subscription: 'subscription { orderUpdated { id } }',
            transport: 'sse',
            reconnectAttempts: 0,
          }),
        { wrapper: createWrapper() },
      );

      await act(async () => {
        instances[0].handler.onopen?.();
      });

      await act(async () => {
        instances[0].handler.onmessage?.({
          data: JSON.stringify({
            errors: [{ message: 'Access denied' }],
          }),
        });
      });

      expect(result.current.error?.message).toBe('Access denied');
    });

    it('transitions to disconnected when SSE connection closes', async () => {
      const { MockEventSource, instances } = createMockEventSource();
      globalThis.EventSource = MockEventSource as unknown as typeof EventSource;

      const { result } = renderHook(
        () =>
          useBifrostSubscription({
            subscription: 'subscription { orderUpdated { id } }',
            transport: 'sse',
            reconnectAttempts: 0,
          }),
        { wrapper: createWrapper() },
      );

      await act(async () => {
        instances[0].handler.onopen?.();
      });

      await act(async () => {
        instances[0].readyState = 2; // EventSource.CLOSED
        instances[0].handler.onerror?.();
      });

      expect(result.current.connectionState).toBe('disconnected');
    });

    it('transitions to error on SSE connection error', async () => {
      const { MockEventSource, instances } = createMockEventSource();
      globalThis.EventSource = MockEventSource as unknown as typeof EventSource;

      const { result } = renderHook(
        () =>
          useBifrostSubscription({
            subscription: 'subscription { orderUpdated { id } }',
            transport: 'sse',
            reconnectAttempts: 0,
          }),
        { wrapper: createWrapper() },
      );

      await act(async () => {
        instances[0].readyState = 0; // CONNECTING (not CLOSED)
        instances[0].handler.onerror?.();
      });

      expect(result.current.connectionState).toBe('error');
      expect(result.current.error?.message).toBe('SSE connection error');
    });

    it('closes EventSource on unmount', () => {
      const { MockEventSource, instances } = createMockEventSource();
      globalThis.EventSource = MockEventSource as unknown as typeof EventSource;

      const { unmount } = renderHook(
        () =>
          useBifrostSubscription({
            subscription: 'subscription { orderUpdated { id } }',
            transport: 'sse',
          }),
        { wrapper: createWrapper() },
      );

      unmount();

      expect(instances[0].close).toHaveBeenCalled();
    });
  });

  describe('auto transport', () => {
    it('uses websocket when WebSocket is available', () => {
      const { MockWebSocket, instances } = createMockWebSocket();
      globalThis.WebSocket = MockWebSocket as unknown as typeof WebSocket;

      renderHook(
        () =>
          useBifrostSubscription({
            subscription: 'subscription { orderUpdated { id } }',
            transport: 'auto',
          }),
        { wrapper: createWrapper() },
      );

      expect(instances).toHaveLength(1);
    });

    it('defaults to auto transport', () => {
      const { MockWebSocket, instances } = createMockWebSocket();
      globalThis.WebSocket = MockWebSocket as unknown as typeof WebSocket;

      renderHook(
        () =>
          useBifrostSubscription({
            subscription: 'subscription { orderUpdated { id } }',
          }),
        { wrapper: createWrapper() },
      );

      expect(instances).toHaveLength(1);
    });
  });

  describe('reconnection', () => {
    it('reconnects after disconnect with exponential backoff', async () => {
      vi.useFakeTimers();
      const { MockWebSocket, instances } = createMockWebSocket();
      globalThis.WebSocket = MockWebSocket as unknown as typeof WebSocket;

      renderHook(
        () =>
          useBifrostSubscription({
            subscription: 'subscription { orderUpdated { id } }',
            transport: 'websocket',
            reconnectAttempts: 3,
            reconnectBaseDelay: 100,
          }),
        { wrapper: createWrapper() },
      );

      expect(instances).toHaveLength(1);

      // Establish connection then disconnect
      await act(async () => {
        instances[0].handler.onopen?.();
        instances[0].handler.onmessage?.({
          data: JSON.stringify({ type: 'connection_ack' }),
        });
      });

      await act(async () => {
        instances[0].handler.onclose?.();
      });

      // First reconnect after 100ms (100 * 2^0)
      expect(instances).toHaveLength(1);

      await act(async () => {
        vi.advanceTimersByTime(100);
      });

      expect(instances).toHaveLength(2);

      vi.useRealTimers();
    });

    it('stops reconnecting after max attempts', async () => {
      vi.useFakeTimers();
      const { MockWebSocket, instances } = createMockWebSocket();
      globalThis.WebSocket = MockWebSocket as unknown as typeof WebSocket;

      renderHook(
        () =>
          useBifrostSubscription({
            subscription: 'subscription { orderUpdated { id } }',
            transport: 'websocket',
            reconnectAttempts: 2,
            reconnectBaseDelay: 50,
          }),
        { wrapper: createWrapper() },
      );

      // Initial connection fails
      await act(async () => {
        instances[0].handler.onerror?.();
      });

      // First reconnect at 50ms
      await act(async () => {
        vi.advanceTimersByTime(50);
      });
      expect(instances).toHaveLength(2);

      // Second failure
      await act(async () => {
        instances[1].handler.onerror?.();
      });

      // Second reconnect at 100ms
      await act(async () => {
        vi.advanceTimersByTime(100);
      });
      expect(instances).toHaveLength(3);

      // Third failure - no more reconnects
      await act(async () => {
        instances[2].handler.onerror?.();
      });

      await act(async () => {
        vi.advanceTimersByTime(10_000);
      });
      expect(instances).toHaveLength(3);

      vi.useRealTimers();
    });

    it('resets reconnect count on successful connection', async () => {
      vi.useFakeTimers();
      const { MockWebSocket, instances } = createMockWebSocket();
      globalThis.WebSocket = MockWebSocket as unknown as typeof WebSocket;

      renderHook(
        () =>
          useBifrostSubscription({
            subscription: 'subscription { orderUpdated { id } }',
            transport: 'websocket',
            reconnectAttempts: 2,
            reconnectBaseDelay: 50,
          }),
        { wrapper: createWrapper() },
      );

      // First connection attempt fails
      await act(async () => {
        instances[0].handler.onerror?.();
      });

      // Reconnect
      await act(async () => {
        vi.advanceTimersByTime(50);
      });
      expect(instances).toHaveLength(2);

      // Second attempt succeeds
      await act(async () => {
        instances[1].handler.onopen?.();
        instances[1].handler.onmessage?.({
          data: JSON.stringify({ type: 'connection_ack' }),
        });
      });

      // Disconnect
      await act(async () => {
        instances[1].handler.onclose?.();
      });

      // Should reconnect again (counter was reset)
      await act(async () => {
        vi.advanceTimersByTime(50);
      });
      expect(instances).toHaveLength(3);

      vi.useRealTimers();
    });

    it('cancels pending reconnect on unmount', async () => {
      vi.useFakeTimers();
      const { MockWebSocket, instances } = createMockWebSocket();
      globalThis.WebSocket = MockWebSocket as unknown as typeof WebSocket;

      const { unmount } = renderHook(
        () =>
          useBifrostSubscription({
            subscription: 'subscription { orderUpdated { id } }',
            transport: 'websocket',
            reconnectAttempts: 3,
            reconnectBaseDelay: 100,
          }),
        { wrapper: createWrapper() },
      );

      await act(async () => {
        instances[0].handler.onerror?.();
      });

      unmount();

      await act(async () => {
        vi.advanceTimersByTime(10_000);
      });

      // Only the initial connection attempt
      expect(instances).toHaveLength(1);

      vi.useRealTimers();
    });
  });

  describe('multiple data updates', () => {
    it('updates data on each new message', async () => {
      const { MockWebSocket, instances } = createMockWebSocket();
      globalThis.WebSocket = MockWebSocket as unknown as typeof WebSocket;

      const dataHistory: Array<{ orderUpdated: { id: number } }> = [];

      const { result } = renderHook(
        () =>
          useBifrostSubscription<{ orderUpdated: { id: number } }>({
            subscription: 'subscription { orderUpdated { id } }',
            transport: 'websocket',
            onData: (d) => dataHistory.push(d),
          }),
        { wrapper: createWrapper() },
      );

      const ws = instances[0];

      await act(async () => {
        ws.handler.onopen?.();
        ws.handler.onmessage?.({
          data: JSON.stringify({ type: 'connection_ack' }),
        });
      });

      await act(async () => {
        ws.handler.onmessage?.({
          data: JSON.stringify({
            type: 'next',
            id: '1',
            payload: { data: { orderUpdated: { id: 1 } } },
          }),
        });
      });

      await act(async () => {
        ws.handler.onmessage?.({
          data: JSON.stringify({
            type: 'next',
            id: '1',
            payload: { data: { orderUpdated: { id: 2 } } },
          }),
        });
      });

      await act(async () => {
        ws.handler.onmessage?.({
          data: JSON.stringify({
            type: 'next',
            id: '1',
            payload: { data: { orderUpdated: { id: 3 } } },
          }),
        });
      });

      expect(result.current.data).toEqual({ orderUpdated: { id: 3 } });
      expect(dataHistory).toHaveLength(3);
      expect(dataHistory[0]).toEqual({ orderUpdated: { id: 1 } });
      expect(dataHistory[1]).toEqual({ orderUpdated: { id: 2 } });
      expect(dataHistory[2]).toEqual({ orderUpdated: { id: 3 } });
    });
  });
});
