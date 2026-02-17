import { useState, useEffect, useRef, useContext, useCallback } from 'react';
import { BifrostContext } from '../components/bifrost-provider';

/**
 * The current state of a subscription connection.
 * - `'connecting'` - Establishing the connection.
 * - `'connected'` - Actively receiving data.
 * - `'disconnected'` - Connection was closed (may auto-reconnect).
 * - `'error'` - Connection failed (may auto-reconnect).
 */
export type ConnectionState =
  | 'connecting'
  | 'connected'
  | 'disconnected'
  | 'error';

/**
 * Transport protocol for subscriptions.
 * - `'websocket'` - Uses the graphql-transport-ws WebSocket protocol.
 * - `'sse'` - Uses Server-Sent Events.
 * - `'auto'` - Selects WebSocket if available, otherwise falls back to SSE.
 */
export type SubscriptionTransport = 'websocket' | 'sse' | 'auto';

/**
 * Options for the {@link useBifrostSubscription} hook.
 * @typeParam T - The expected subscription data type.
 */
export interface UseBifrostSubscriptionOptions<T = unknown> {
  /** The GraphQL subscription query string. */
  subscription: string;
  /** Optional GraphQL variables for the subscription. */
  variables?: Record<string, unknown>;
  /** Transport protocol. Defaults to `'auto'`. */
  transport?: SubscriptionTransport;
  /** Whether the subscription should connect. Defaults to `true`. */
  enabled?: boolean;
  /** Callback invoked each time new data is received. */
  onData?: (data: T) => void;
  /** Callback invoked on connection errors. */
  onError?: (error: Error) => void;
  /** Maximum number of reconnection attempts. Defaults to `5`. */
  reconnectAttempts?: number;
  /** Base delay in milliseconds for reconnection backoff. Defaults to `1000`. */
  reconnectBaseDelay?: number;
}

interface SubscriptionResult<T> {
  data: T | undefined;
  connectionState: ConnectionState;
  isConnected: boolean;
  error: Error | undefined;
}

interface ConnectionRef {
  close: () => void;
}

function buildWsUrl(httpEndpoint: string): string {
  const url = new URL(httpEndpoint);
  url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
  return url.toString();
}

function reconnectDelay(attempt: number, baseDelay: number): number {
  return Math.min(baseDelay * 2 ** attempt, 30_000);
}

function createWebSocketConnection<T>(
  endpoint: string,
  headers: Record<string, string>,
  subscription: string,
  variables: Record<string, unknown> | undefined,
  callbacks: {
    onStateChange: (state: ConnectionState) => void;
    onData: (data: T) => void;
    onError: (error: Error) => void;
  },
): ConnectionRef {
  const wsUrl = buildWsUrl(endpoint);
  const ws = new WebSocket(wsUrl, 'graphql-transport-ws');
  let acknowledged = false;

  callbacks.onStateChange('connecting');

  ws.onopen = () => {
    ws.send(
      JSON.stringify({
        type: 'connection_init',
        payload: Object.keys(headers).length > 0 ? headers : undefined,
      }),
    );
  };

  ws.onmessage = (event: MessageEvent) => {
    let msg: { type: string; id?: string; payload?: unknown };
    try {
      msg = JSON.parse(event.data as string);
    } catch {
      return;
    }

    switch (msg.type) {
      case 'connection_ack':
        acknowledged = true;
        callbacks.onStateChange('connected');
        ws.send(
          JSON.stringify({
            type: 'subscribe',
            id: '1',
            payload: {
              query: subscription,
              variables,
            },
          }),
        );
        break;

      case 'next':
        if (
          msg.payload &&
          typeof msg.payload === 'object' &&
          'data' in msg.payload
        ) {
          callbacks.onData((msg.payload as { data: T }).data);
        }
        break;

      case 'error': {
        const errors = msg.payload as Array<{ message: string }> | undefined;
        const message =
          errors?.map((e) => e.message).join(', ') ?? 'Subscription error';
        callbacks.onError(new Error(message));
        break;
      }

      case 'complete':
        break;

      case 'ping':
        ws.send(JSON.stringify({ type: 'pong' }));
        break;
    }
  };

  ws.onerror = () => {
    if (!acknowledged) {
      callbacks.onStateChange('error');
      callbacks.onError(new Error('WebSocket connection failed'));
    }
  };

  ws.onclose = () => {
    if (acknowledged) {
      callbacks.onStateChange('disconnected');
    }
  };

  return {
    close: () => {
      if (ws.readyState === WebSocket.OPEN) {
        ws.send(JSON.stringify({ type: 'complete', id: '1' }));
      }
      ws.close();
    },
  };
}

function createSSEConnection<T>(
  endpoint: string,
  subscription: string,
  variables: Record<string, unknown> | undefined,
  callbacks: {
    onStateChange: (state: ConnectionState) => void;
    onData: (data: T) => void;
    onError: (error: Error) => void;
  },
): ConnectionRef {
  const params = new URLSearchParams({ query: subscription });
  if (variables && Object.keys(variables).length > 0) {
    params.set('variables', JSON.stringify(variables));
  }

  const sseUrl = `${endpoint}?${params.toString()}`;

  callbacks.onStateChange('connecting');

  const eventSource = new EventSource(sseUrl);

  eventSource.onopen = () => {
    callbacks.onStateChange('connected');
  };

  eventSource.onmessage = (event: MessageEvent) => {
    let parsed: { data?: T; errors?: Array<{ message: string }> };
    try {
      parsed = JSON.parse(event.data as string);
    } catch {
      return;
    }

    if (parsed.errors) {
      callbacks.onError(
        new Error(parsed.errors.map((e) => e.message).join(', ')),
      );
      return;
    }

    if (parsed.data !== undefined) {
      callbacks.onData(parsed.data);
    }
  };

  eventSource.onerror = () => {
    if (eventSource.readyState === EventSource.CLOSED) {
      callbacks.onStateChange('disconnected');
    } else {
      callbacks.onStateChange('error');
      callbacks.onError(new Error('SSE connection error'));
    }
  };

  return {
    close: () => {
      eventSource.close();
    },
  };
}

/**
 * Hook for real-time data via WebSocket or Server-Sent Events.
 *
 * Manages the connection lifecycle including automatic reconnection with
 * exponential backoff (capped at 30 seconds). The reconnect counter resets
 * on successful connection.
 *
 * Must be used within a {@link BifrostProvider}.
 *
 * @typeParam T - The expected subscription data type.
 * @param options - Subscription configuration.
 * @returns The latest data, connection state, and any error.
 *
 * @example
 * ```tsx
 * const { data, connectionState, isConnected, error } = useBifrostSubscription<{
 *   orderUpdated: Order;
 * }>({
 *   subscription: 'subscription { orderUpdated { id status total } }',
 *   transport: 'auto',
 *   onData: (data) => console.log('Update:', data),
 * });
 * ```
 */
export function useBifrostSubscription<T = unknown>(
  options: UseBifrostSubscriptionOptions<T>,
): SubscriptionResult<T> {
  const config = useContext(BifrostContext);
  if (!config) {
    throw new Error(
      'useBifrostSubscription must be used within a BifrostProvider',
    );
  }

  const {
    subscription,
    variables,
    transport = 'auto',
    enabled = true,
    onData: onDataCallback,
    onError: onErrorCallback,
    reconnectAttempts = 5,
    reconnectBaseDelay = 1000,
  } = options;

  const [data, setData] = useState<T | undefined>(undefined);
  const [connectionState, setConnectionState] =
    useState<ConnectionState>('disconnected');
  const [error, setError] = useState<Error | undefined>(undefined);

  const connectionRef = useRef<ConnectionRef | null>(null);
  const reconnectCountRef = useRef(0);
  const reconnectTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const mountedRef = useRef(true);

  const onDataCallbackRef = useRef(onDataCallback);
  onDataCallbackRef.current = onDataCallback;

  const onErrorCallbackRef = useRef(onErrorCallback);
  onErrorCallbackRef.current = onErrorCallback;

  const variablesKey = JSON.stringify(variables);
  const headersKey = JSON.stringify(config.headers);

  const connect = useCallback(
    (chosenTransport: 'websocket' | 'sse') => {
      connectionRef.current?.close();

      const currentHeaders: Record<string, string> = config.headers ?? {};
      const currentVariables: Record<string, unknown> | undefined = variables;

      const callbacks = {
        onStateChange: (state: ConnectionState) => {
          if (!mountedRef.current) return;
          setConnectionState(state);

          if (state === 'disconnected' || state === 'error') {
            if (reconnectCountRef.current < reconnectAttempts) {
              const delay = reconnectDelay(
                reconnectCountRef.current,
                reconnectBaseDelay,
              );
              reconnectCountRef.current++;
              reconnectTimerRef.current = setTimeout(() => {
                if (mountedRef.current) {
                  connect(chosenTransport);
                }
              }, delay);
            }
          }

          if (state === 'connected') {
            reconnectCountRef.current = 0;
          }
        },
        onData: (newData: T) => {
          if (!mountedRef.current) return;
          setData(newData);
          setError(undefined);
          onDataCallbackRef.current?.(newData);
        },
        onError: (err: Error) => {
          if (!mountedRef.current) return;
          setError(err);
          onErrorCallbackRef.current?.(err);
        },
      };

      if (chosenTransport === 'websocket') {
        connectionRef.current = createWebSocketConnection<T>(
          config.endpoint,
          currentHeaders,
          subscription,
          currentVariables,
          callbacks,
        );
      } else {
        connectionRef.current = createSSEConnection<T>(
          config.endpoint,
          subscription,
          currentVariables,
          callbacks,
        );
      }
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [
      config.endpoint,
      headersKey,
      subscription,
      variablesKey,
      reconnectAttempts,
      reconnectBaseDelay,
    ],
  );

  useEffect(() => {
    mountedRef.current = true;

    if (!enabled) {
      setConnectionState('disconnected');
      return;
    }

    const chosenTransport =
      transport === 'auto'
        ? typeof WebSocket !== 'undefined'
          ? 'websocket'
          : 'sse'
        : transport;

    reconnectCountRef.current = 0;
    connect(chosenTransport);

    return () => {
      mountedRef.current = false;
      if (reconnectTimerRef.current !== null) {
        clearTimeout(reconnectTimerRef.current);
        reconnectTimerRef.current = null;
      }
      connectionRef.current?.close();
      connectionRef.current = null;
    };
  }, [enabled, transport, connect]);

  return {
    data,
    connectionState,
    isConnected: connectionState === 'connected',
    error,
  };
}
