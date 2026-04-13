/**
 * Pluggable GraphQL transport layer for the BifrostQL.UI frontend.
 *
 * Two implementations:
 *
 * - `HttpTransport` issues GraphQL over HTTP/JSON via `fetch`. This is the
 *   default and matches the historical behavior of the UI. It is stateless
 *   so `connected` is always true.
 *
 * - `BinaryTransport` issues GraphQL over the WebSocket binary protobuf
 *   protocol via `@bifrostql/binary-client`. The underlying client is opened
 *   lazily on the first `query()` call so just constructing the transport
 *   has no side effects, and `connected` reflects the live WebSocket state
 *   for the health indicator.
 *
 * The factory returns a `QueryTransport` and the toggle UI persists the
 * chosen mode in `localStorage` under `bifrost-ui:transport`. HTTP is the
 * default for backwards compatibility; users opt-in to binary explicitly.
 *
 * Note on integration: as of this commit the existing `Editor` component
 * from `@standardbeagle/edit-db` constructs its own GraphQL client from the
 * `uri` prop and does not yet accept a transport instance. The actual
 * routing of editor queries through this abstraction is tracked as a
 * follow-up — see the TODO at the Editor render site in App.tsx. The
 * factory and health indicator land here so the binary client gets
 * exercised and the wiring is ready for the editor migration.
 */

import {
  BifrostBinaryClient,
  type BifrostQueryResult,
} from "@bifrostql/binary-client";

/**
 * The shape every transport must implement. Both modes return the same
 * envelope so call sites can be written once and routed at runtime.
 */
export interface QueryTransport {
  /**
   * Executes a GraphQL operation. The transport decides whether the request
   * goes over HTTP or the binary WebSocket; callers don't need to care.
   */
  query(
    text: string,
    variables?: Record<string, unknown>
  ): Promise<QueryTransportResult>;

  /**
   * Releases any underlying resources. Safe to call multiple times. After
   * `close()` the transport is no longer usable.
   */
  close(): void;

  /**
   * Whether the transport currently has an open connection. HTTP transports
   * are stateless and always report `true`; binary transports report the
   * live WebSocket state and update as the underlying client connects,
   * disconnects, or reconnects.
   */
  readonly connected: boolean;

  /**
   * Identifier used by the UI badge so it can render different text per
   * mode without doing instanceof checks.
   */
  readonly mode: TransportMode;
}

/**
 * Common result envelope. Mirrors `BifrostQueryResult` so the binary path
 * can pass it through unchanged, and the HTTP path adapts a standard
 * GraphQL JSON response into the same shape.
 */
export interface QueryTransportResult {
  data: unknown;
  errors: string[];
}

export type TransportMode = "http" | "binary";

/**
 * Configuration shared by both transport implementations. `endpoint` is the
 * absolute base URL used to reach the BifrostQL server (typically
 * `window.location.origin`). The factory derives the GraphQL HTTP URL and
 * the binary WebSocket URL from this single value so the toggle UI only has
 * to think about one input.
 */
export interface TransportConfig {
  /** Origin of the BifrostQL server, e.g. `https://example.com`. */
  endpoint: string;
  /**
   * Path appended to `endpoint` for HTTP GraphQL requests. Defaults to
   * `/graphql` to match the existing app behavior.
   */
  graphqlPath?: string;
  /**
   * Path appended to `endpoint` (with the protocol swapped to ws/wss) for
   * the binary WebSocket. Defaults to `/bifrost-ws` to match the server's
   * BifrostHttpMiddleware mapping.
   */
  binaryPath?: string;
}

/** localStorage key the toggle UI reads and writes. */
export const TRANSPORT_STORAGE_KEY = "bifrost-ui:transport";

/** Default transport mode when nothing is persisted yet. */
export const DEFAULT_TRANSPORT_MODE: TransportMode = "http";

/**
 * Reads the persisted transport mode, falling back to the default if no
 * value is stored, the value is malformed, or `localStorage` is unavailable
 * (e.g. SSR, tests, blocked storage). Never throws.
 */
export function loadTransportMode(
  storage: Pick<Storage, "getItem"> | null = safeLocalStorage()
): TransportMode {
  if (!storage) {
    return DEFAULT_TRANSPORT_MODE;
  }
  try {
    const raw = storage.getItem(TRANSPORT_STORAGE_KEY);
    return normalizeTransportMode(raw);
  } catch {
    return DEFAULT_TRANSPORT_MODE;
  }
}

/**
 * Writes the chosen transport mode to localStorage. Silently no-ops if
 * storage is unavailable or the write fails (quota, blocked storage).
 */
export function saveTransportMode(
  mode: TransportMode,
  storage: Pick<Storage, "setItem"> | null = safeLocalStorage()
): void {
  if (!storage) {
    return;
  }
  try {
    storage.setItem(TRANSPORT_STORAGE_KEY, mode);
  } catch {
    // Storage quota exceeded or blocked. Persisting is a nice-to-have, not
    // a correctness requirement, so swallowing is appropriate here.
  }
}

/**
 * Coerces an unknown localStorage value into a valid `TransportMode`. Any
 * unrecognized or null value resolves to the default.
 */
export function normalizeTransportMode(raw: string | null): TransportMode {
  if (raw === "binary" || raw === "http") {
    return raw;
  }
  return DEFAULT_TRANSPORT_MODE;
}

/**
 * Derives the WebSocket URL for the binary transport from an HTTP origin.
 * Maps `http://` → `ws://` and `https://` → `wss://`, and appends the
 * binary path. Used by the factory and exported so tests can verify the
 * derivation independently.
 */
export function deriveBinaryUrl(endpoint: string, binaryPath: string): string {
  const trimmed = endpoint.replace(/\/+$/, "");
  const wsBase = trimmed
    .replace(/^https:/i, "wss:")
    .replace(/^http:/i, "ws:");
  const path = binaryPath.startsWith("/") ? binaryPath : `/${binaryPath}`;
  return `${wsBase}${path}`;
}

/**
 * Returns the global `localStorage` object if present, or null in
 * environments where it isn't (Node, JSDOM with storage disabled, browsers
 * with privacy mode). Wraps the access in a try/catch because reading
 * `localStorage` can itself throw when storage is blocked by the browser.
 */
function safeLocalStorage(): Storage | null {
  try {
    if (typeof globalThis === "undefined") {
      return null;
    }
    const candidate = (globalThis as { localStorage?: Storage }).localStorage;
    return candidate ?? null;
  } catch {
    return null;
  }
}

/**
 * GraphQL-over-HTTP transport. Posts `{ query, variables }` to the GraphQL
 * endpoint and adapts the standard `{ data, errors }` JSON response into a
 * `QueryTransportResult`. Stateless, so `connected` is always true.
 */
export class HttpTransport implements QueryTransport {
  readonly mode: TransportMode = "http";
  private readonly url: string;

  constructor(url: string) {
    this.url = url;
  }

  get connected(): boolean {
    return true;
  }

  async query(
    text: string,
    variables?: Record<string, unknown>
  ): Promise<QueryTransportResult> {
    const response = await fetch(this.url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ query: text, variables }),
    });

    if (!response.ok) {
      // Surface HTTP-level failures as transport errors rather than letting
      // a non-2xx response masquerade as a successful empty body. Callers
      // already expect to inspect `errors` so this slots in cleanly.
      return {
        data: null,
        errors: [`HTTP ${response.status} ${response.statusText}`],
      };
    }

    const body = (await response.json()) as {
      data?: unknown;
      errors?: Array<{ message: string } | string>;
    };
    return {
      data: body.data ?? null,
      errors: (body.errors ?? []).map((e) =>
        typeof e === "string" ? e : e.message
      ),
    };
  }

  close(): void {
    // Nothing to release for the HTTP transport.
  }
}

/**
 * GraphQL-over-binary-WebSocket transport. Wraps `BifrostBinaryClient` and
 * lazily opens the underlying WebSocket on the first `query()` call so that
 * just constructing the transport has no side effects.
 *
 * The `connected` getter delegates straight through to the binary client so
 * the UI badge reflects the real socket state — including disconnects and
 * the auto-reconnect cycle.
 */
export class BinaryTransport implements QueryTransport {
  readonly mode: TransportMode = "binary";
  private client: BifrostBinaryClient | null = null;
  private connectPromise: Promise<void> | null = null;
  private closed = false;

  constructor(
    private readonly url: string,
    private readonly clientFactory: (
      url: string
    ) => BifrostBinaryClient = defaultBinaryClientFactory
  ) {}

  get connected(): boolean {
    return this.client?.connected ?? false;
  }

  async query(
    text: string,
    variables?: Record<string, unknown>
  ): Promise<QueryTransportResult> {
    if (this.closed) {
      throw new Error("BinaryTransport is closed");
    }
    await this.ensureConnected();
    // After ensureConnected we know the client is non-null; the cast keeps
    // strict null checks happy without an explicit assertion at the use
    // site.
    const result = (await this.client!.query(
      text,
      variables
    )) as BifrostQueryResult;
    return { data: result.data, errors: result.errors };
  }

  close(): void {
    this.closed = true;
    this.connectPromise = null;
    if (this.client) {
      this.client.close();
      this.client = null;
    }
  }

  /**
   * Opens the underlying WebSocket if it isn't open yet. Coalesces
   * concurrent first calls onto the same in-flight connect promise so we
   * don't accidentally open two sockets when several queries are issued
   * back-to-back from the UI.
   */
  private async ensureConnected(): Promise<void> {
    if (this.client && this.client.connected) {
      return;
    }
    if (!this.connectPromise) {
      this.client = this.clientFactory(this.url);
      this.connectPromise = this.client.connect().catch((err) => {
        // Reset state so a subsequent query can retry from scratch instead
        // of being stuck behind a permanently-rejected promise.
        this.client = null;
        this.connectPromise = null;
        throw err;
      });
    }
    await this.connectPromise;
  }
}

function defaultBinaryClientFactory(url: string): BifrostBinaryClient {
  return new BifrostBinaryClient({ url });
}

/**
 * Creates a transport for the requested mode. The factory is the single
 * entry point used by the UI: it resolves the absolute URLs from the
 * `endpoint` config and wires up the right implementation.
 *
 * Optional `binaryClientFactory` exists so tests can inject a fake binary
 * client without spinning up a real WebSocket.
 */
export function createTransport(
  mode: TransportMode,
  config: TransportConfig,
  binaryClientFactory?: (url: string) => BifrostBinaryClient
): QueryTransport {
  const graphqlPath = config.graphqlPath ?? "/graphql";
  const binaryPath = config.binaryPath ?? "/bifrost-ws";
  const httpUrl = `${config.endpoint.replace(/\/+$/, "")}${
    graphqlPath.startsWith("/") ? graphqlPath : `/${graphqlPath}`
  }`;

  if (mode === "binary") {
    const wsUrl = deriveBinaryUrl(config.endpoint, binaryPath);
    return new BinaryTransport(wsUrl, binaryClientFactory);
  }
  return new HttpTransport(httpUrl);
}
