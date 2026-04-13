/**
 * Async-iterator streaming API for chunked BifrostQL responses.
 *
 * The default `query()` / `mutate()` path on BifrostBinaryClient resolves a
 * single Promise once a chunked response has been fully reassembled. For large
 * binary payloads (images, file downloads, generated reports) the consumer
 * often wants to process bytes as soon as each chunk lands instead of waiting
 * for the full transfer.
 *
 * `createChunkStream` registers a per-requestId StreamingQueue with the client
 * and returns an `AsyncIterableIterator<StreamChunk>` that yields each
 * verified chunk in sequence order. Out-of-order wire delivery is buffered
 * internally so the consumer always sees `sequence: 0, 1, 2, ...` ascending.
 *
 * ## Wire path
 *
 * The client routes incoming Chunk frames to either `pending` (promise mode)
 * or `streamingQueues` (this module) based on which API method was used to
 * issue the request. Streaming chunks bypass `ChunkReassembler` entirely:
 * each chunk is CRC32-verified inline and pushed straight to the queue. This
 * avoids allocating the full `totalBytes` reassembly buffer for stream-only
 * consumers and keeps the streaming path independent of promise-mode state.
 *
 * ## Backpressure
 *
 * The queue holds up to `MAX_QUEUE_SIZE` (256) buffered chunks. If the
 * producer outruns the consumer beyond that limit, the queue errors with a
 * "stream backpressure exceeded" message and the iterator throws on the next
 * `next()` call. 256 chunks at the server's default 64 KB chunk size is 16 MB
 * of in-memory buffering, which is enough headroom for any realistic consumer
 * loop while still bounding memory in pathological cases.
 *
 * The simpler alternative — applying real backpressure via a producer-side
 * Promise that the consumer must drain — would require the WebSocket message
 * handler to await the queue, which is incompatible with the synchronous
 * `onmessage` callback shape. Erroring on overflow is the only correct option
 * given the underlying transport.
 */
import { emptyChunkInfo, verifyChecksum } from "./chunking.js";
import {
  BifrostMessageType,
  encodeMessage,
  type BifrostMessage,
} from "./index.js";

/**
 * One verified chunk delivered to a streaming consumer.
 */
export interface StreamChunk {
  /** The request_id of the originating query/mutation. */
  requestId: number;
  /** 0-based chunk index. Always emitted in ascending order. */
  sequence: number;
  /** Total number of chunks in this transfer. */
  totalChunks: number;
  /** Raw payload bytes of this chunk. */
  bytes: Uint8Array;
  /** True only for the chunk where `sequence === totalChunks - 1`. */
  isLast: boolean;
}

/** Maximum number of buffered chunks before the queue errors. */
export const MAX_QUEUE_SIZE = 256;

/**
 * Internal state contract the StreamingQueue needs from BifrostBinaryClient.
 *
 * Defined as an interface (rather than importing the class) so streaming.ts
 * doesn't depend on the full client type and so tests can drive the queue
 * with a minimal stub. The real client implements all of these.
 */
export interface StreamingClientHooks {
  readonly connected: boolean;
  /** Called when the consumer breaks out of the loop or the stream errors. */
  removeStreamingQueue(requestId: number): void;
}

/**
 * A bounded async queue of StreamChunks that supports a single consumer using
 * `for await ... of queue`. Producers call `push`, `error`, or `complete`;
 * consumers iterate via the AsyncIterableIterator interface.
 *
 * Out-of-order chunks are buffered by sequence number and only released to the
 * consumer when the next contiguous chunk has arrived, so the iterator always
 * yields `sequence: 0, 1, 2, ...` ascending.
 */
export class StreamingQueue implements AsyncIterableIterator<StreamChunk> {
  private readonly pendingChunks = new Map<number, StreamChunk>();
  private readonly ready: StreamChunk[] = [];
  private nextSequence = 0;
  private totalChunks = -1;
  private closed = false;
  private errorValue: Error | null = null;
  private waiter: {
    resolve: (result: IteratorResult<StreamChunk>) => void;
    reject: (err: Error) => void;
  } | null = null;
  private cleanup: (() => void) | null = null;

  constructor(
    public readonly requestId: number,
    cleanup: () => void
  ) {
    this.cleanup = cleanup;
  }

  /** Total buffered + ready chunks; used by overflow detection and tests. */
  get bufferedCount(): number {
    return this.pendingChunks.size + this.ready.length;
  }

  /**
   * Accepts a verified chunk frame from the client. Buffers out-of-order
   * arrivals until the next contiguous sequence is available, then drains
   * any newly-contiguous run into the ready queue and wakes the consumer.
   *
   * Auto-completes once the queue has received every chunk (nextSequence
   * advances to totalChunks), so callers don't have to call `complete()`
   * after the final chunk arrives.
   */
  push(chunk: StreamChunk): void {
    if (this.closed || this.errorValue !== null) {
      return;
    }
    if (this.totalChunks === -1) {
      this.totalChunks = chunk.totalChunks;
    }
    if (this.bufferedCount >= MAX_QUEUE_SIZE) {
      this.error(
        new Error(
          `Streaming queue for request ${this.requestId} exceeded MAX_QUEUE_SIZE (${MAX_QUEUE_SIZE} chunks)`
        )
      );
      return;
    }

    this.pendingChunks.set(chunk.sequence, chunk);
    this.drainContiguous();
    if (
      this.totalChunks > 0 &&
      this.nextSequence === this.totalChunks &&
      this.pendingChunks.size === 0
    ) {
      this.complete();
      return;
    }
    this.wakeConsumer();
  }

  /**
   * Signals that no more chunks will arrive normally (the stream completed).
   * Any buffered chunks already in `ready` are still delivered first; the
   * iterator terminates only after the consumer has drained everything.
   * Runs the cleanup hook so the client drops its registration.
   */
  complete(): void {
    if (this.closed || this.errorValue !== null) {
      return;
    }
    this.closed = true;
    this.runCleanup();
    this.wakeConsumer();
  }

  /**
   * Aborts the stream with an error. The next `next()` call (or in-progress
   * await) rejects with this error after any already-ready chunks have been
   * drained. Out-of-order pending chunks (not yet contiguous) are dropped.
   * Runs the cleanup hook so the client drops its registration.
   */
  error(err: Error): void {
    if (this.errorValue !== null) {
      return;
    }
    this.errorValue = err;
    this.pendingChunks.clear();
    this.runCleanup();
    this.wakeConsumer();
  }

  /**
   * Pulls every contiguous chunk starting at `nextSequence` out of the
   * pending map and appends to the ready queue.
   */
  private drainContiguous(): void {
    while (this.pendingChunks.has(this.nextSequence)) {
      const chunk = this.pendingChunks.get(this.nextSequence)!;
      this.pendingChunks.delete(this.nextSequence);
      this.ready.push(chunk);
      this.nextSequence++;
    }
  }

  private wakeConsumer(): void {
    if (!this.waiter) {
      return;
    }
    const waiter = this.waiter;
    this.waiter = null;

    // Always drain ready chunks first, even if an error is set, so the
    // consumer sees every chunk that arrived before the failure.
    const next = this.ready.shift();
    if (next !== undefined) {
      waiter.resolve({ value: next, done: false });
      return;
    }
    if (this.errorValue !== null) {
      waiter.reject(this.errorValue);
      return;
    }
    if (this.closed) {
      waiter.resolve({ value: undefined, done: true });
    }
  }

  next(): Promise<IteratorResult<StreamChunk>> {
    // Drain any ready chunks first, even when the stream is in an error
    // state, so consumers see every chunk that arrived before the failure.
    const next = this.ready.shift();
    if (next !== undefined) {
      return Promise.resolve({ value: next, done: false });
    }
    if (this.errorValue !== null) {
      return Promise.reject(this.errorValue);
    }
    if (this.closed) {
      return Promise.resolve({ value: undefined, done: true });
    }
    return new Promise((resolve, reject) => {
      this.waiter = { resolve, reject };
    });
  }

  /**
   * Called when the consumer breaks out of the `for await` loop. Cleans up
   * the client-side registration so the stream is cancelled.
   */
  return(): Promise<IteratorResult<StreamChunk>> {
    this.closed = true;
    this.ready.length = 0;
    this.pendingChunks.clear();
    this.runCleanup();
    if (this.waiter) {
      const waiter = this.waiter;
      this.waiter = null;
      waiter.resolve({ value: undefined, done: true });
    }
    return Promise.resolve({ value: undefined, done: true });
  }

  /**
   * Called when the consumer's `for await` body throws. Mirrors `return()`
   * semantics: drop state and cancel the registration.
   */
  throw(err?: unknown): Promise<IteratorResult<StreamChunk>> {
    const wrapped = err instanceof Error ? err : new Error(String(err));
    this.error(wrapped);
    this.runCleanup();
    return Promise.reject(wrapped);
  }

  [Symbol.asyncIterator](): AsyncIterableIterator<StreamChunk> {
    return this;
  }

  private runCleanup(): void {
    if (this.cleanup) {
      const fn = this.cleanup;
      this.cleanup = null;
      fn();
    }
  }
}

/**
 * Validates a chunked frame and pushes it onto a StreamingQueue. Mirrors the
 * inline checks performed by `ChunkReassembler.addChunk` (CRC32, sequence
 * range, duplicate detection) so the streaming path stays self-contained and
 * doesn't depend on the reassembler buffer.
 *
 * Returns true if the chunk was accepted (or was a benign duplicate); false
 * if validation failed and the queue was errored.
 */
export function ingestStreamingChunk(
  queue: StreamingQueue,
  message: BifrostMessage
): boolean {
  const info = message.chunkInfo;
  if (info.total <= 0) {
    queue.error(
      new Error(
        `Streaming chunk for request ${message.requestId} has total=${info.total}`
      )
    );
    return false;
  }
  if (info.sequence < 0 || info.sequence >= info.total) {
    queue.error(
      new Error(
        `Streaming chunk sequence ${info.sequence} out of range [0, ${info.total}) for request ${message.requestId}`
      )
    );
    return false;
  }
  if (!verifyChecksum(message.payload, info.checksum)) {
    queue.error(
      new Error(
        `CRC32 mismatch on streaming chunk ${info.sequence} for request ${message.requestId}`
      )
    );
    return false;
  }

  queue.push({
    requestId: message.requestId,
    sequence: info.sequence,
    totalChunks: info.total,
    bytes: message.payload,
    isLast: info.sequence === info.total - 1,
  });
  return true;
}

/**
 * Internal client surface needed by `createChunkStream`. The real
 * BifrostBinaryClient exposes all of these via the methods listed below.
 */
export interface StreamingClientInternals extends StreamingClientHooks {
  registerStreamingQueue(requestId: number, queue: StreamingQueue): void;
  allocateRequestId(): number;
  sendRawFrame(bytes: Uint8Array): void;
}

/**
 * Allocates a new request, registers a StreamingQueue with the client, sends
 * the initial Query/Mutation frame, and returns the iterator. Used by
 * `BifrostBinaryClient.stream()` and `streamMutation()`.
 */
export function createChunkStream(
  client: StreamingClientInternals,
  type: BifrostMessageType,
  queryText: string,
  variables?: Record<string, unknown>
): AsyncIterableIterator<StreamChunk> {
  if (!client.connected) {
    const failed = new StreamingQueue(0, () => {});
    failed.error(new Error("Not connected"));
    return failed;
  }

  const requestId = client.allocateRequestId();
  const queue = new StreamingQueue(requestId, () => {
    client.removeStreamingQueue(requestId);
  });
  client.registerStreamingQueue(requestId, queue);

  const frame: BifrostMessage = {
    requestId,
    type,
    query: queryText,
    variablesJson: variables ? JSON.stringify(variables) : "",
    payload: new Uint8Array(0),
    errors: [],
    chunkInfo: emptyChunkInfo(),
  };
  client.sendRawFrame(encodeMessage(frame));
  return queue;
}
