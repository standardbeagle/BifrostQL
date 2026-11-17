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
 * The server stops sending after `ackWindow` (default 8) unacked chunks. The
 * queue acks a chunk only when it hands it to the consumer (`next()`), so a
 * slow consumer slows the server down and the queue holds at most about
 * `ackWindow` chunks (512 KB at the default 64 KB chunk size). The message
 * handler never awaits: it buffers the chunk, and the consumer's pull sends
 * the ack.
 *
 * When the stream stops early (consumer `break`, error, timeout) every held
 * chunk is acked at once and the client ack-drops the rest, so the server can
 * finish. Two limits follow from the server's 30 s ack timeout, which closes
 * the whole connection: a consumer must not hold one chunk (stop calling
 * `next()`) longer than that, and an iterator it abandons must be ended with
 * `break` / `return()` rather than left suspended. The client's own idle
 * timeout (`requestTimeoutMs`) restarts on every chunk received or acked.
 *
 * `MAX_QUEUE_SIZE` (256) stays as a guard against a server that ignores its
 * window: past it the queue errors and the iterator throws.
 */
import { emptyChunkInfo, verifyChecksum } from "./chunking.js";
import { StreamPayloadExtractor } from "./stream-payload.js";
import {
  BifrostMessageType,
  encodeMessage,
  MAX_REQUEST_FRAME_BYTES,
  oversizedRequestError,
  type BifrostMessage,
  type RequestMetadata,
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
  /**
   * This chunk's share of the response payload (the GraphQL response JSON).
   * Concatenating `bytes` over every chunk gives the same bytes whether or
   * not the server chunked the response. The first chunk of a chunked
   * response is shorter than the rest: the protobuf envelope it also
   * carried is stripped.
   */
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
  /**
   * Called when the stream ends — normal completion, consumer break, or error.
   * `completed` is true only when the final chunk was received; a false value
   * signals an early teardown so the client can tombstone the id and ack-drop
   * any chunks the server is still sending.
   */
  removeStreamingQueue(requestId: number, completed?: boolean): void;
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
  /**
   * Set once a chunked frame arrives: chunk bytes are slices of the
   * serialized message, so the envelope is stripped as chunks drain in order.
   */
  private extractor: StreamPayloadExtractor | null = null;
  /**
   * Sequences of chunked fragments received but not yet acked. A fragment is
   * acked when the consumer takes it, or when the stream ends holding it.
   */
  private readonly owedAcks = new Set<number>();
  private readonly ackChunk: ((sequence: number) => void) | null;

  /**
   * @param ackChunk sends the ChunkAck for a fragment. Omitted for queues that
   *   only ever see unchunked frames, which the server does not expect acks
   *   for.
   */
  constructor(
    public readonly requestId: number,
    cleanup: () => void,
    ackChunk?: (sequence: number) => void
  ) {
    this.cleanup = cleanup;
    this.ackChunk = ackChunk ?? null;
  }

  /** Total buffered + ready chunks; used by overflow detection and tests. */
  get bufferedCount(): number {
    return this.pendingChunks.size + this.ready.length;
  }

  /**
   * True once any chunk has been accepted, contiguous or not. The reconnect
   * path fails such a stream instead of re-running it: the server cannot
   * resume a transfer on a new socket, and a re-run would deliver the same
   * chunks again.
   */
  get receivedAnyChunk(): boolean {
    return this.nextSequence > 0 || this.pendingChunks.size > 0;
  }

  /**
   * True once every chunk of the declared total has been received (the final
   * chunk drained into the ready buffer). The client uses this at teardown to
   * decide whether a tombstone is needed: a fully-received stream means the
   * server has stopped sending, so no late chunks will arrive. An early
   * teardown (consumer break, overflow, error) leaves this false.
   */
  get streamCompleted(): boolean {
    return this.totalChunks > 0 && this.nextSequence >= this.totalChunks;
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
    // Drop a chunk whose sequence was already consumed (a duplicate or a
    // server retransmit). drainContiguous only ever pulls sequences at
    // or above nextSequence, so a stale one parked in pendingChunks would sit
    // there forever — keeping pendingChunks.size > 0 so the auto-complete check
    // never fires and the consumer hangs until the idle timeout.
    if (chunk.sequence < this.nextSequence) {
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
    if (this.errorValue !== null) {
      return;
    }
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
   * Accepts a verified fragment of a chunked response. Its bytes are a slice
   * of the serialized BifrostMessage; the consumer receives only the payload
   * part (see StreamPayloadExtractor).
   *
   * The fragment's ack is owed until the consumer takes it, so the server's
   * ack window (8 chunks by default) bounds what this queue buffers. A
   * duplicate of a fragment still held is dropped (its ack is already owed);
   * a duplicate of one already taken is acked at once, as the server expects
   * an ack per frame it sent.
   */
  pushFragment(chunk: StreamChunk): void {
    this.extractor ??= new StreamPayloadExtractor();
    if (this.owedAcks.has(chunk.sequence)) {
      return;
    }
    if (
      this.closed ||
      this.errorValue !== null ||
      chunk.sequence < this.nextSequence
    ) {
      this.ackChunk?.(chunk.sequence);
      return;
    }
    this.owedAcks.add(chunk.sequence);
    this.push(chunk);
  }

  /**
   * Drops the acks still owed without sending them. Called when the
   * connection that carried the fragments is gone: an ack on the next socket
   * would name a transfer that socket never started.
   */
  forgetOwedAcks(): void {
    this.owedAcks.clear();
  }

  /** Acks a fragment as it is handed to the consumer. */
  private handOut(chunk: StreamChunk): StreamChunk {
    if (this.owedAcks.delete(chunk.sequence)) {
      this.ackChunk?.(chunk.sequence);
    }
    return chunk;
  }

  /**
   * Acks every fragment still held. Called when the stream stops early
   * (error, consumer break): backpressure no longer matters, and an unacked
   * fragment would stall the server's transfer until its ack timeout closes
   * the whole connection.
   */
  private releaseOwedAcks(): void {
    const owed = Array.from(this.owedAcks);
    this.owedAcks.clear();
    for (const sequence of owed) {
      this.ackChunk?.(sequence);
    }
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
   * Every held fragment is acked now, so the server can finish the transfer.
   * Runs the cleanup hook so the client drops its registration.
   */
  error(err: Error): void {
    if (this.errorValue !== null) {
      return;
    }
    this.errorValue = err;
    this.pendingChunks.clear();
    this.releaseOwedAcks();
    this.runCleanup();
    this.wakeConsumer();
  }

  /**
   * Pulls every contiguous chunk starting at `nextSequence` out of the
   * pending map and appends to the ready queue, stripping the envelope from
   * chunked fragments in order. An envelope that carries server errors, or
   * is malformed, errors the queue.
   */
  private drainContiguous(): void {
    while (this.pendingChunks.has(this.nextSequence)) {
      const chunk = this.pendingChunks.get(this.nextSequence)!;
      this.pendingChunks.delete(this.nextSequence);
      this.nextSequence++;
      if (this.extractor) {
        try {
          chunk.bytes = this.extractor.take(chunk.bytes);
          if (chunk.isLast) this.extractor.finish();
        } catch (err) {
          this.error(err instanceof Error ? err : new Error(String(err)));
          return;
        }
      }
      this.ready.push(chunk);
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
      waiter.resolve({ value: this.handOut(next), done: false });
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
      return Promise.resolve({ value: this.handOut(next), done: false });
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
   * the client-side registration so the stream is cancelled, and acks every
   * fragment still held so the server's transfer is not left waiting.
   */
  return(): Promise<IteratorResult<StreamChunk>> {
    this.closed = true;
    this.ready.length = 0;
    this.pendingChunks.clear();
    this.releaseOwedAcks();
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

  queue.pushFragment({
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
  /**
   * Registers the queue and sends the request frame, or queues it until the
   * server has a free concurrency slot.
   */
  startStreamingRequest(
    requestId: number,
    queue: StreamingQueue,
    meta: RequestMetadata
  ): void;
  allocateRequestId(): number;
  /** Sends the ChunkAck for a fragment the consumer has taken. */
  ackStreamChunk(requestId: number, sequence: number): void;
}

/**
 * Allocates a new request, registers a StreamingQueue with the client, starts
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
  const frame: BifrostMessage = {
    requestId,
    type,
    query: queryText,
    variablesJson: variables ? JSON.stringify(variables) : "",
    payload: new Uint8Array(0),
    errors: [],
    chunkInfo: emptyChunkInfo(),
    lastSequence: 0,
    errorCode: "",
  };
  const bytes = encodeMessage(frame);
  if (bytes.length > MAX_REQUEST_FRAME_BYTES) {
    const failed = new StreamingQueue(requestId, () => {});
    failed.error(oversizedRequestError(bytes.length));
    return failed;
  }

  const queue = new StreamingQueue(
    requestId,
    () => {
      // Pass whether the stream reached its final chunk so the client only
      // tombstones the id on an early teardown (break / overflow / error).
      client.removeStreamingQueue(requestId, queue.streamCompleted);
    },
    (sequence) => client.ackStreamChunk(requestId, sequence)
  );
  client.startStreamingRequest(requestId, queue, { type, frame: bytes });
  return queue;
}
