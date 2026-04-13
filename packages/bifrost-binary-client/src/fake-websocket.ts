/**
 * In-memory fake WebSocket for testing BifrostBinaryClient without a live server.
 *
 * Matches the minimal `IWebSocket` / `IWebSocketConstructor` shape consumed by the
 * client (see src/index.ts). Tests drive the fake by calling `simulateOpen`,
 * `simulateError`, `simulateClose`, and `receive` to inject server-originated frames,
 * and inspect outbound frames via the `sentFrames` array.
 *
 * The constructor itself is exposed via `createFakeWebSocketConstructor()` so each
 * test can grab a fresh tracker that records every fake created during the test.
 */

export const FakeWebSocketReadyState = {
  CONNECTING: 0,
  OPEN: 1,
  CLOSING: 2,
  CLOSED: 3,
} as const;

export interface FakeWebSocketCloseEvent {
  code: number;
  reason: string;
}

export class FakeWebSocket {
  static OPEN = FakeWebSocketReadyState.OPEN;

  readyState: number = FakeWebSocketReadyState.CONNECTING;
  binaryType: string = "blob";
  onopen: ((ev: unknown) => void) | null = null;
  onclose: ((ev: FakeWebSocketCloseEvent) => void) | null = null;
  onerror: ((ev: unknown) => void) | null = null;
  onmessage: ((ev: { data: unknown }) => void) | null = null;

  readonly url: string;
  readonly sentFrames: Uint8Array[] = [];
  closeCalls: Array<{ code?: number; reason?: string }> = [];

  constructor(url: string) {
    this.url = url;
  }

  send(data: Uint8Array | ArrayBuffer): void {
    if (this.readyState !== FakeWebSocketReadyState.OPEN) {
      throw new Error("FakeWebSocket.send called while not OPEN");
    }
    const bytes =
      data instanceof Uint8Array
        ? new Uint8Array(data) // copy so caller mutations can't change captured frames
        : new Uint8Array(data);
    this.sentFrames.push(bytes);
  }

  close(code?: number, reason?: string): void {
    this.closeCalls.push({ code, reason });
    if (
      this.readyState === FakeWebSocketReadyState.CLOSED ||
      this.readyState === FakeWebSocketReadyState.CLOSING
    ) {
      return;
    }
    this.readyState = FakeWebSocketReadyState.CLOSING;
    // Mirror real WebSocket: closing transitions through CLOSING then fires onclose
    // when fully closed. We invoke onclose synchronously so tests stay deterministic;
    // tests that need to control timing can avoid calling close() and call
    // simulateClose() instead.
    this.readyState = FakeWebSocketReadyState.CLOSED;
    this.onclose?.({ code: code ?? 1005, reason: reason ?? "" });
  }

  /** Test helper: transition to OPEN and fire onopen. */
  simulateOpen(): void {
    if (this.readyState === FakeWebSocketReadyState.OPEN) {
      return;
    }
    this.readyState = FakeWebSocketReadyState.OPEN;
    this.onopen?.({});
  }

  /** Test helper: fire onerror without changing readyState. */
  simulateError(err?: unknown): void {
    this.onerror?.(err ?? {});
  }

  /** Test helper: fire onclose with the given code/reason and transition to CLOSED. */
  simulateClose(code = 1006, reason = "abnormal"): void {
    this.readyState = FakeWebSocketReadyState.CLOSED;
    this.onclose?.({ code, reason });
  }

  /** Test helper: deliver an inbound binary frame to the client. */
  receive(bytes: Uint8Array): void {
    if (!this.onmessage) {
      throw new Error("FakeWebSocket.receive called before onmessage was registered");
    }
    // Match the client expectation: event.data is an ArrayBuffer.
    const buffer = bytes.buffer.slice(
      bytes.byteOffset,
      bytes.byteOffset + bytes.byteLength
    );
    this.onmessage({ data: buffer });
  }
}

/**
 * Returns a fresh tracker that exposes a constructor compatible with
 * `IWebSocketConstructor` and records every fake instance created.
 */
export interface FakeWebSocketTracker {
  ctor: { new (url: string): FakeWebSocket; readonly OPEN: number };
  instances: FakeWebSocket[];
  /** Convenience accessor for the most recently created instance. */
  readonly last: FakeWebSocket;
}

export function createFakeWebSocketConstructor(): FakeWebSocketTracker {
  const instances: FakeWebSocket[] = [];

  // Tracking subclass so each invocation of `new ctor(url)` is recorded.
  class TrackedFakeWebSocket extends FakeWebSocket {
    constructor(url: string) {
      super(url);
      instances.push(this);
    }
  }

  const ctor = TrackedFakeWebSocket as unknown as {
    new (url: string): FakeWebSocket;
    readonly OPEN: number;
  };

  return {
    ctor,
    instances,
    get last(): FakeWebSocket {
      const ws = instances[instances.length - 1];
      if (!ws) {
        throw new Error("No FakeWebSocket instances have been created yet");
      }
      return ws;
    },
  };
}
