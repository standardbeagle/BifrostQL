/**
 * Promise-based wrapper around the Photino native bridge.
 *
 * Photino injects two primitives on `window.external`:
 *
 *   - `sendMessage(string)` — webview → host (one way, fire-and-forget).
 *   - `receiveMessage(callback)` — host → webview (one callback, all msgs).
 *
 * This module layers a request/response protocol on top:
 *
 *   wire shape: `{ id: string, kind: string, payload?: unknown }`
 *
 * Each outbound request gets a UUID `id`. The host echoes that id back on
 * `{ kind: "result" }` (success) or `{ kind: "error" }` (failure). Unsolicited
 * events from the host carry a host-generated id (the webview ignores it)
 * and any non-result/non-error `kind`; they're fanned out to subscribers
 * registered via `onBridgeEvent`.
 *
 * Design notes:
 *
 * - **Single init**: `window.external.receiveMessage` only supports one
 *   dispatch callback. We register exactly one, the very first time the
 *   module is used, and demultiplex inside it. Subsequent calls to any
 *   exported function reuse the same dispatcher.
 *
 * - **Lazy init**: we don't touch `window.external` at import time, so the
 *   module is safe to import in SSR/vitest environments where `window` is
 *   absent. `isBridgeAvailable` is the probe; `sendBridgeRequest` and
 *   `onBridgeEvent` throw/short-circuit if the bridge isn't available.
 *
 * - **Timeout**: every request carries its own `setTimeout` so a dead host
 *   doesn't leak pending promises forever. Default is 10s, override via
 *   the `options.timeoutMs` argument.
 *
 * - **Malformed inbound**: the dispatcher swallows JSON parse failures and
 *   shape mismatches with a console warning. A single bad message must not
 *   take down the subscription — otherwise one corrupt host push would
 *   kill the entire webview-to-host channel.
 */

export interface BridgeRequestOptions {
  /**
   * Hard deadline in milliseconds after which the request rejects with a
   * `BridgeError("timeout", ...)`. Default 10000.
   */
  timeoutMs?: number;
}

/**
 * Error thrown when a bridge request fails. The `kind` mirrors the wire
 * `kind` for host-originated failures (`"error"`) and uses synthetic names
 * (`"timeout"`, `"unavailable"`) for client-side faults so call sites can
 * branch on failure mode without string-matching the message.
 */
export class BridgeError extends Error {
  constructor(
    public readonly kind: string,
    message: string
  ) {
    super(message);
    this.name = "BridgeError";
  }
}

// Default request timeout. Chosen to be comfortably longer than any
// reasonable host-side handler while still bounded enough that a
// genuinely-hung C# task surfaces as a UI error rather than a
// silently-leaking promise.
const DEFAULT_TIMEOUT_MS = 10_000;

// Shape of the wire envelope. `payload` is typed as unknown because it can
// be anything the caller passes, including primitives (number, string) as
// well as objects — the C# side mirrors it with System.Text.Json.JsonElement.
interface Envelope {
  id: string;
  kind: string;
  payload?: unknown;
}

// A single outstanding request. We keep resolve/reject and the timeout
// handle together so the dispatcher can fire the right one and cancel the
// timer atomically.
interface PendingRequest {
  resolve: (value: unknown) => void;
  reject: (reason: unknown) => void;
  timeoutHandle: ReturnType<typeof setTimeout>;
}

// Module-scoped dispatcher state. Intentionally not exported — the public
// API is the three functions below. Tests reset this state by using
// `vi.resetModules()` + dynamic import to get a fresh module instance.
const pending = new Map<string, PendingRequest>();
const listeners = new Map<string, Set<(payload: unknown) => void>>();
let dispatcherInstalled = false;

/**
 * Probes whether a Photino native bridge is present. Safe to call in any
 * environment: returns `false` in Node, SSR, or unit tests that haven't
 * installed a fake `window.external`.
 */
export function isBridgeAvailable(): boolean {
  if (typeof window === "undefined") {
    return false;
  }
  const ext = (window as Window).external;
  return (
    !!ext &&
    typeof ext.sendMessage === "function" &&
    typeof ext.receiveMessage === "function"
  );
}

/**
 * Sends a request to the host and returns a promise that resolves with the
 * host's `result` payload or rejects with a `BridgeError`.
 *
 * @throws `BridgeError("unavailable", ...)` if no bridge is present.
 */
export function sendBridgeRequest<T = unknown>(
  kind: string,
  payload?: unknown,
  options?: BridgeRequestOptions
): Promise<T> {
  if (!isBridgeAvailable()) {
    return Promise.reject(
      new BridgeError(
        "unavailable",
        "Photino native bridge is not available in this environment"
      )
    );
  }

  ensureDispatcherInstalled();

  const id = generateRequestId();
  const envelope: Envelope = { id, kind, payload };
  const timeoutMs = options?.timeoutMs ?? DEFAULT_TIMEOUT_MS;

  return new Promise<T>((resolve, reject) => {
    // Arm the timeout first so that even a synchronously-throwing
    // sendMessage (should not happen, but defensive) leaves no dangling
    // entry in the pending map.
    const timeoutHandle = setTimeout(() => {
      pending.delete(id);
      reject(
        new BridgeError(
          "timeout",
          `Bridge request "${kind}" (${id}) timed out after ${timeoutMs}ms`
        )
      );
    }, timeoutMs);

    pending.set(id, {
      resolve: resolve as (value: unknown) => void,
      reject,
      timeoutHandle,
    });

    try {
      window.external.sendMessage(JSON.stringify(envelope));
    } catch (err) {
      clearTimeout(timeoutHandle);
      pending.delete(id);
      reject(
        new BridgeError(
          "send-failed",
          err instanceof Error ? err.message : String(err)
        )
      );
    }
  });
}

/**
 * Subscribes to unsolicited host events of a given kind. Returns an
 * unsubscribe function. Multiple subscribers may share the same kind; they
 * receive the payload in registration order.
 */
export function onBridgeEvent(
  kind: string,
  handler: (payload: unknown) => void
): () => void {
  if (!isBridgeAvailable()) {
    // Subscribing in an environment without a bridge is a silent no-op
    // rather than an exception — call sites can wire handlers
    // unconditionally and the absence just means "no events will fire".
    return () => {
      /* noop */
    };
  }
  ensureDispatcherInstalled();

  let set = listeners.get(kind);
  if (!set) {
    set = new Set();
    listeners.set(kind, set);
  }
  set.add(handler);

  return () => {
    const current = listeners.get(kind);
    if (!current) return;
    current.delete(handler);
    if (current.size === 0) {
      listeners.delete(kind);
    }
  };
}

/**
 * Registers the single receiveMessage dispatcher with the Photino bridge.
 * Safe to call repeatedly — only the first call wires up the real handler.
 */
function ensureDispatcherInstalled(): void {
  if (dispatcherInstalled) return;
  if (!isBridgeAvailable()) return;
  window.external.receiveMessage(dispatchInbound);
  dispatcherInstalled = true;
}

/**
 * Core inbound dispatch. Parses the JSON envelope, routes `result`/`error`
 * back to the owning pending request, and fans out all other kinds to the
 * event listeners. Any failure mode — malformed JSON, unknown id, handler
 * throw — is isolated so the subscription stays live.
 */
function dispatchInbound(raw: string): void {
  let envelope: Envelope;
  try {
    envelope = JSON.parse(raw) as Envelope;
  } catch {
    // Host sent non-JSON. Nothing we can correlate, so log and drop.
    // Not using console.error because this is recoverable noise.
    console.warn("[native-bridge] dropped malformed inbound message", raw);
    return;
  }

  if (
    !envelope ||
    typeof envelope.id !== "string" ||
    typeof envelope.kind !== "string"
  ) {
    console.warn("[native-bridge] dropped envelope with bad shape", envelope);
    return;
  }

  if (envelope.kind === "result" || envelope.kind === "error") {
    const request = pending.get(envelope.id);
    if (!request) {
      // Response to a request we've already timed out or never made. Drop
      // silently — this is expected when a slow handler finally replies
      // after the timer has fired.
      return;
    }
    clearTimeout(request.timeoutHandle);
    pending.delete(envelope.id);
    if (envelope.kind === "result") {
      request.resolve(envelope.payload);
    } else {
      const message = extractErrorMessage(envelope.payload);
      request.reject(new BridgeError("error", message));
    }
    return;
  }

  const set = listeners.get(envelope.kind);
  if (!set || set.size === 0) {
    // Unsubscribed event kind. Not worth warning about — the host is
    // allowed to push events the current UI screen doesn't care about.
    return;
  }
  for (const handler of set) {
    try {
      handler(envelope.payload);
    } catch (err) {
      // A listener blowing up must not wedge siblings or the dispatcher.
      console.error(
        `[native-bridge] listener for "${envelope.kind}" threw`,
        err
      );
    }
  }
}

/**
 * Pulls a human-readable message out of an `error` envelope payload. The
 * host is expected to send `{ message: string }` but we defensively handle
 * other shapes so a broken host doesn't surface as `undefined` in the UI.
 */
function extractErrorMessage(payload: unknown): string {
  if (
    payload &&
    typeof payload === "object" &&
    "message" in payload &&
    typeof (payload as { message: unknown }).message === "string"
  ) {
    return (payload as { message: string }).message;
  }
  return "Bridge returned an error with no message";
}

/**
 * Generates a short-lived correlation id. Prefers `crypto.randomUUID` when
 * it's available (standard in modern browsers and Node 19+); falls back to
 * a random+timestamp composite that's unique enough for the per-session
 * scale we operate at. Deliberately not a cryptographic guarantee — these
 * ids only need to collide-avoid within a single webview instance.
 */
function generateRequestId(): string {
  const c = (globalThis as { crypto?: { randomUUID?: () => string } }).crypto;
  if (c && typeof c.randomUUID === "function") {
    return c.randomUUID();
  }
  return `${Math.random().toString(36).slice(2)}-${Date.now().toString(36)}`;
}
