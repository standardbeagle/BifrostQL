/**
 * Auto-reconnect with exponential backoff and RESUME protocol for
 * BifrostBinaryClient.
 *
 * ## RESUME wire format
 *
 * Discovered from `src/BifrostQL.Server/BifrostMessage.cs` and the Resume
 * handler in `src/BifrostQL.Server/BifrostBinaryMiddleware.cs` (the `if
 * (request.Type == BifrostMessageType.Resume)` branch).
 *
 * A Resume frame is a regular `BifrostMessage` envelope with:
 *   - field  1 (request_id, varint)   — the original request_id whose chunked
 *                                        transfer we want to resume.
 *   - field  2 (type, varint)         — 6 (BifrostMessageType.Resume).
 *   - field 12 (last_sequence, varint) — the highest contiguous chunk sequence
 *                                        the client has already received. The
 *                                        server retransmits everything strictly
 *                                        after this value (i.e. starting at
 *                                        `last_sequence + 1`).
 *
 * A sentinel value of `0xFFFFFFFF` (`uint.MaxValue` on the server) for
 * `last_sequence` means "no chunks received yet, retransmit from sequence 0".
 * `ChunkBuffer.GetChunksAfter` interprets the sentinel exactly that way.
 *
 * The server replies with a `ResumeAck` (type 7) frame whose `chunk_total`
 * field carries the count of remaining chunks it is about to retransmit
 * (0 if the entry has expired or is unknown). The actual retransmitted chunks
 * follow as ordinary `Chunk` frames addressed to the same `request_id`, so the
 * existing `handleChunkedFrame` path on the client picks them up without any
 * resume-specific code.
 *
 * ## Reconnect lifecycle
 *
 * `ReconnectController` owns a tiny state machine:
 *
 *   idle → waiting → connecting → idle (on success)
 *                  ↘ idle (on cancel)
 *                  ↘ giving-up (on max attempts exceeded)
 *
 * The controller does not create WebSockets itself; instead it is given a
 * `connect` callback that returns a Promise. The client wires that callback
 * to its own internal `tryConnect` so all WebSocket lifecycle stays inside
 * the client. This keeps the controller a pure scheduler that is trivial to
 * unit-test with `vi.useFakeTimers()`.
 */

/**
 * Computes the delay (in milliseconds) before the next reconnect attempt.
 * Implementations may be deterministic (tests) or jittered (production).
 */
export interface BackoffPolicy {
  /**
   * Returns the delay in milliseconds before attempt number `attempt`. The
   * first reconnect attempt after a disconnect is attempt 1.
   */
  nextDelayMs(attempt: number): number;
  /** Resets any internal state. Called when a reconnect succeeds. */
  reset(): void;
}

/**
 * Options for `ExponentialBackoff`. All fields are optional; defaults match
 * the values quoted in the task brief: 100 ms initial, doubling, 30 s cap,
 * 25 % jitter. Jitter is symmetric around the base delay (i.e. the returned
 * value is in `[base * (1 - jitter), base * (1 + jitter)]`).
 */
export interface ExponentialBackoffOptions {
  /** Delay for attempt 1, in milliseconds. Defaults to 100. */
  initialMs?: number;
  /** Maximum delay regardless of attempt count. Defaults to 30_000. */
  maxMs?: number;
  /** Multiplier applied each attempt. Defaults to 2. */
  factor?: number;
  /**
   * Jitter ratio in `[0, 1]`. The returned delay is multiplied by a value in
   * `[1 - jitter, 1 + jitter]`. Defaults to 0.25.
   */
  jitter?: number;
  /**
   * Source of randomness in `[0, 1)`. Defaults to `Math.random`. Tests pass
   * a deterministic generator (e.g. `() => 0.5`) so the produced sequence is
   * reproducible.
   */
  random?: () => number;
}

/**
 * Truncated exponential backoff with symmetric jitter. The base sequence
 * (without jitter) for the defaults is 100, 200, 400, 800, 1600, 3200, 6400,
 * 12800, 25600, 30000, 30000, ... ms.
 */
export class ExponentialBackoff implements BackoffPolicy {
  private readonly initialMs: number;
  private readonly maxMs: number;
  private readonly factor: number;
  private readonly jitter: number;
  private readonly random: () => number;

  constructor(options: ExponentialBackoffOptions = {}) {
    this.initialMs = options.initialMs ?? 100;
    this.maxMs = options.maxMs ?? 30_000;
    this.factor = options.factor ?? 2;
    this.jitter = options.jitter ?? 0.25;
    this.random = options.random ?? Math.random;
    if (this.initialMs <= 0) {
      throw new Error("ExponentialBackoff requires initialMs > 0");
    }
    if (this.maxMs < this.initialMs) {
      throw new Error("ExponentialBackoff requires maxMs >= initialMs");
    }
    if (this.factor < 1) {
      throw new Error("ExponentialBackoff requires factor >= 1");
    }
    if (this.jitter < 0 || this.jitter > 1) {
      throw new Error("ExponentialBackoff requires jitter in [0, 1]");
    }
  }

  nextDelayMs(attempt: number): number {
    if (attempt < 1) {
      throw new Error(`Backoff attempt must be >= 1, got ${attempt}`);
    }
    const exponent = attempt - 1;
    // Math.pow rather than a loop because attempt counts can be large after
    // sustained outages and the cap clamps the result anyway.
    const raw = this.initialMs * Math.pow(this.factor, exponent);
    const base = Math.min(raw, this.maxMs);
    if (this.jitter === 0) {
      return base;
    }
    // Symmetric jitter: random in [0, 1) → multiplier in [1 - j, 1 + j).
    const multiplier = 1 - this.jitter + this.random() * (2 * this.jitter);
    return Math.max(0, Math.floor(base * multiplier));
  }

  reset(): void {
    // Stateless implementation; reset is a no-op so callers can swap to a
    // stateful policy without touching call sites.
  }
}

/**
 * Lifecycle states for the reconnect state machine. Exposed only for tests.
 */
export type ReconnectState = "idle" | "waiting" | "connecting" | "stopped";

/**
 * Callback fired by `ReconnectController` to actually open a new WebSocket.
 * Resolves on connect, rejects on connect failure (which triggers another
 * scheduled retry).
 */
export type ReconnectAttemptFn = () => Promise<void>;

/**
 * Options for `ReconnectController`. The controller takes the policy, the
 * connect callback, and a few hook callbacks; it owns the timer and the
 * attempt counter.
 */
export interface ReconnectControllerOptions {
  policy: BackoffPolicy;
  /** The function used to open a fresh WebSocket on each retry. */
  connect: ReconnectAttemptFn;
  /** Hard cap on attempts before giving up. Defaults to Infinity. */
  maxAttempts?: number;
  /** Fired right before the connect callback is invoked. */
  onAttempt?: (attempt: number, delayMs: number) => void;
  /** Fired after a successful reconnect. */
  onSuccess?: (attempt: number) => void;
  /** Fired when `maxAttempts` is exhausted; the controller transitions to stopped. */
  onGiveUp?: (attempts: number, lastError: Error) => void;
  /**
   * Timer factory. Defaults to the global `setTimeout`/`clearTimeout`. Tests
   * can pass `vi.useFakeTimers()`-controlled timers transparently because the
   * defaults already pick those up.
   */
  setTimer?: (fn: () => void, ms: number) => unknown;
  clearTimer?: (handle: unknown) => void;
}

/**
 * Drives the reconnect loop using a `BackoffPolicy`. After a non-normal
 * disconnect the client constructs a controller, calls `start(error)`, and
 * lets the controller schedule retries via `setTimeout`. On success the
 * controller transitions back to `idle` and resets the policy; on failure
 * it schedules another retry. `cancel()` aborts any pending retry and is
 * idempotent.
 */
export class ReconnectController {
  private state: ReconnectState = "idle";
  private attempt = 0;
  private timer: unknown = null;
  private readonly policy: BackoffPolicy;
  private readonly connect: ReconnectAttemptFn;
  private readonly maxAttempts: number;
  private readonly onAttempt?: (attempt: number, delayMs: number) => void;
  private readonly onSuccess?: (attempt: number) => void;
  private readonly onGiveUp?: (attempts: number, lastError: Error) => void;
  private readonly setTimer: (fn: () => void, ms: number) => unknown;
  private readonly clearTimer: (handle: unknown) => void;

  constructor(options: ReconnectControllerOptions) {
    this.policy = options.policy;
    this.connect = options.connect;
    this.maxAttempts = options.maxAttempts ?? Number.POSITIVE_INFINITY;
    this.onAttempt = options.onAttempt;
    this.onSuccess = options.onSuccess;
    this.onGiveUp = options.onGiveUp;
    // Default to globalThis timer functions wrapped in lambdas so the
    // identity check inside Node's setTimeout doesn't reject `this`.
    this.setTimer =
      options.setTimer ??
      ((fn: () => void, ms: number) =>
        (globalThis as unknown as {
          setTimeout: (cb: () => void, ms: number) => unknown;
        }).setTimeout(fn, ms));
    this.clearTimer =
      options.clearTimer ??
      ((handle: unknown) => {
        (globalThis as unknown as {
          clearTimeout: (h: unknown) => void;
        }).clearTimeout(handle);
      });
  }

  /** The current state, exposed for diagnostics and tests. */
  get currentState(): ReconnectState {
    return this.state;
  }

  /** The number of attempts made since the last successful connect. */
  get attemptCount(): number {
    return this.attempt;
  }

  /**
   * Starts (or continues) the reconnect loop. Idempotent if already in
   * `waiting` or `connecting`. The `lastError` is the error that triggered
   * the current disconnect; it is propagated to `onGiveUp` if the controller
   * eventually exhausts its retry budget without a successful attempt
   * intervening.
   */
  start(lastError: Error): void {
    if (this.state === "stopped") {
      return;
    }
    if (this.state === "waiting" || this.state === "connecting") {
      return;
    }
    this.scheduleNext(lastError);
  }

  /**
   * Cancels any pending retry and transitions to `idle`. Safe to call from
   * any state. Tests may also call this to stop a controller mid-loop.
   */
  cancel(): void {
    if (this.timer !== null) {
      this.clearTimer(this.timer);
      this.timer = null;
    }
    this.state = "idle";
    this.attempt = 0;
    this.policy.reset();
  }

  /**
   * Permanently stops the controller. Unlike `cancel()`, a stopped controller
   * cannot be restarted; this is used after `client.close()` so that any
   * in-flight `connect` resolution that races with close cannot accidentally
   * re-open the connection.
   */
  stop(): void {
    this.cancel();
    this.state = "stopped";
  }

  private scheduleNext(lastError: Error): void {
    if (this.state === "stopped") {
      return;
    }
    if (this.attempt >= this.maxAttempts) {
      this.giveUp(lastError);
      return;
    }
    this.attempt++;
    const delay = this.policy.nextDelayMs(this.attempt);
    this.state = "waiting";
    this.onAttempt?.(this.attempt, delay);
    this.timer = this.setTimer(() => {
      this.timer = null;
      this.runAttempt(lastError);
    }, delay);
  }

  private runAttempt(lastError: Error): void {
    if (this.state === "stopped") {
      return;
    }
    this.state = "connecting";
    this.connect().then(
      () => {
        if (this.state === "stopped") {
          return;
        }
        const completedAttempt = this.attempt;
        this.state = "idle";
        this.attempt = 0;
        this.policy.reset();
        this.onSuccess?.(completedAttempt);
      },
      (err: unknown) => {
        if (this.state === "stopped") {
          return;
        }
        const wrapped = err instanceof Error ? err : new Error(String(err));
        // Stay in the loop: schedule another retry with an incremented attempt.
        // We move back through `idle` so scheduleNext's guard accepts the call.
        this.state = "idle";
        this.scheduleNext(wrapped);
        // Preserve the original triggering error for context, but use the
        // latest failure for any final onGiveUp call by storing on the closure.
        // (We pass `wrapped` here so the most recent error is what surfaces.)
        // The `lastError` parameter from the outer call is effectively shadowed.
        void lastError;
      }
    );
  }

  private giveUp(lastError: Error): void {
    const total = this.attempt;
    this.state = "stopped";
    this.attempt = 0;
    this.onGiveUp?.(total, lastError);
  }
}
