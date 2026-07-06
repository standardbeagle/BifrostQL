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
 * Editor integration: `TransportGraphQLFetcher` (transport-fetcher.ts) adapts
 * a `QueryTransport` to the edit-db `GraphQLFetcher` interface and is injected
 * into `<Editor fetcher=...>` by App.tsx, so all editor data paths route
 * through the selected transport.
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
   *
   * `options.signal` cancels a superseded request — react-query passes one per
   * query so stale fetches (rapid paging/filtering) are aborted. Both the HTTP
   * and binary transports honor it.
   */
  query(
    text: string,
    variables?: Record<string, unknown>,
    options?: QueryTransportOptions
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

/** Per-request options common to every transport. */
export interface QueryTransportOptions {
  /** Aborts the request when fired (superseded/unmounted queries). */
  signal?: AbortSignal;
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
 * Determines whether a GraphQL document's operation is a mutation. Strips the
 * ignored tokens that can legally precede the operation keyword — whitespace,
 * commas, and `#` line comments — and skips any fragment definitions that
 * precede the operation, then tests for a leading `mutation` keyword.
 *
 * The binary transport uses this to pick the wire message type: the binary
 * client's reconnect logic applies an at-most-once guard to Mutation-typed
 * frames (an interrupted mutation is rejected, never replayed), so sending a
 * GraphQL mutation as a Query-typed frame would let a reconnect silently
 * re-execute it.
 */
export function isMutationDocument(text: string): boolean {
  let rest = stripIgnored(text);
  // Fragment definitions may legally precede the operation; skip each one
  // (keyword through its balanced selection set) so a fragment-first mutation
  // document isn't misclassified as a query and stripped of the at-most-once
  // guard.
  while (/^fragment\b/i.test(rest)) {
    const afterBody = skipBraceBlock(rest);
    if (afterBody === null) {
      // Malformed/unbalanced document — classify conservatively as a query.
      return false;
    }
    rest = stripIgnored(afterBody);
  }
  return /^mutation\b/i.test(rest);
}

/** Drops leading GraphQL ignored tokens: whitespace, commas, # comments. */
function stripIgnored(text: string): string {
  return text.replace(/^(?:[\s,]+|#[^\n\r]*)+/, "");
}

/**
 * Skips past the first `{ ... }` block in `text` (brace-counted, ignoring
 * braces inside string literals) and returns the remainder, or null when no
 * balanced block is found.
 */
function skipBraceBlock(text: string): string | null {
  const open = text.indexOf("{");
  if (open === -1) return null;
  let depth = 0;
  let inString = false;
  for (let i = open; i < text.length; i++) {
    const ch = text[i];
    if (inString) {
      if (ch === "\\") i++;
      else if (ch === '"') inString = false;
      continue;
    }
    if (ch === '"') inString = true;
    else if (ch === "{") depth++;
    else if (ch === "}") {
      depth--;
      if (depth === 0) return text.slice(i + 1);
    }
  }
  return null;
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
    variables?: Record<string, unknown>,
    options?: QueryTransportOptions
  ): Promise<QueryTransportResult> {
    const response = await fetch(this.url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ query: text, variables }),
      signal: options?.signal,
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
    variables?: Record<string, unknown>,
    options?: QueryTransportOptions
  ): Promise<QueryTransportResult> {
    if (this.closed) {
      throw new Error("BinaryTransport is closed");
    }
    await this.ensureConnected();
    // GraphQL mutations must travel as Mutation-typed frames so the binary
    // client's at-most-once reconnect guard applies. Sending them as Query
    // frames would let an interrupted mutation be replayed on reconnect and
    // execute twice on the server.
    const result: BifrostQueryResult = isMutationDocument(text)
      ? await this.client!.mutate(text, variables, options?.signal)
      : await this.client!.query(text, variables, options?.signal);
    return { data: result.data, errors: result.errors };
  }

  close(): void {
    this.closed = true;
    if (this.client) {
      this.client.close();
      this.client = null;
    }
  }

  /**
   * Lazily builds the client and delegates connection management to it. The
   * client owns all connect state: `connect()` short-circuits when the socket
   * is already OPEN, dedupes concurrent opens onto one in-flight promise, and
   * re-arms a reconnect controller that previously gave up. Keeping a second
   * latch here would be a parallel state machine chasing the same socket. On
   * failure we simply rethrow — the client instance is kept so its own
   * retry/replay design (rearm on the next connect) stays intact; closing it
   * would permanently stop the reconnect controller.
   */
  private async ensureConnected(): Promise<void> {
    if (!this.client) {
      this.client = this.clientFactory(this.url);
    }
    await this.client.connect();
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
