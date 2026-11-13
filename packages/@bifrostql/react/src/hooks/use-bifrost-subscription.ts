import {
  useState,
  useEffect,
  useLayoutEffect,
  useRef,
  useContext,
  useCallback,
} from 'react';
import { BifrostContext } from '../components/bifrost-provider';
import {
  BifrostGraphQLError,
  type GraphQLErrorEntry,
} from '../utils/graphql-client';

/**
 * The current state of a subscription connection.
 * - `'connecting'` - Establishing the connection.
 * - `'connected'` - Actively receiving data.
 * - `'disconnected'` - Connection was closed (may auto-reconnect).
 * - `'error'` - Connection failed (may auto-reconnect).
 */
export type ConnectionState =
  'connecting' | 'connected' | 'disconnected' | 'error';

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
        const errors = msg.payload as GraphQLErrorEntry[] | undefined;
        callbacks.onError(
          errors
            ? new BifrostGraphQLError(errors)
            : new Error('Subscription error'),
        );
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
      // Detach every handler before closing. A deliberate teardown otherwise
      // fires `onclose` with `acknowledged === true`, which the caller reads
      // as an unexpected drop and answers with a reconnect — one that closes
      // over this connection's now-superseded subscription and variables.
      ws.onopen = null;
      ws.onmessage = null;
      ws.onerror = null;
      ws.onclose = null;
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
    let parsed: { data?: T; errors?: GraphQLErrorEntry[] };
    try {
      parsed = JSON.parse(event.data as string);
    } catch {
      return;
    }

    if (parsed.errors) {
      callbacks.onError(new BifrostGraphQLError(parsed.errors));
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
      // Same reasoning as the WebSocket path: a deliberate close must not be
      // reported back as a dropped connection worth reconnecting.
      eventSource.onopen = null;
      eventSource.onmessage = null;
      eventSource.onerror = null;
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

  // Latest callbacks, read by transport events that fire after commit.
  const onDataCallbackRef = useRef(onDataCallback);
  const onErrorCallbackRef = useRef(onErrorCallback);
  useLayoutEffect(() => {
    onDataCallbackRef.current = onDataCallback;
    onErrorCallbackRef.current = onErrorCallback;
  });

  // Disabling drops the connection; report it in the same render rather than
  // from the effect that tears it down.
  const [prevEnabled, setPrevEnabled] = useState(enabled);
  if (enabled !== prevEnabled) {
    setPrevEnabled(enabled);
    if (!enabled) setConnectionState('disconnected');
  }

  const variablesKey = JSON.stringify(variables);
  const headersKey = JSON.stringify(config.headers);

  const connect = useCallback(
    (initialTransport: 'websocket' | 'sse') => {
      const endpoint = config.endpoint;
      const currentHeaders: Record<string, string> = config.headers ?? {};
      const currentVariables: Record<string, unknown> | undefined = variables;

      // A named inner function so the reconnect timer can call it again
      // without the callback referring to its own binding.
      function open(chosenTransport: 'websocket' | 'sse') {
        connectionRef.current?.close();

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
                // Overwriting the ref without clearing orphaned the previous
                // timer, which still fired and opened a second connection.
                if (reconnectTimerRef.current !== null) {
                  clearTimeout(reconnectTimerRef.current);
                }
                reconnectTimerRef.current = setTimeout(() => {
                  reconnectTimerRef.current = null;
                  if (mountedRef.current) {
                    open(chosenTransport);
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
            endpoint,
            currentHeaders,
            subscription,
            currentVariables,
            callbacks,
          );
        } else {
          connectionRef.current = createSSEConnection<T>(
            endpoint,
            subscription,
            currentVariables,
            callbacks,
          );
        }
      }
      open(initialTransport);
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

    if (!enabled) return;

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
