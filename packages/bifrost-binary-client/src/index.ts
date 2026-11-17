import { BinaryReader, BinaryWriter, WireType } from "@bufbuild/protobuf/wire";
import {
  CHUNK_FIELD,
  ChunkReassembler,
  crc32,
  emptyChunkInfo,
  isChunkedFrame,
  type ChunkInfo,
} from "./chunking.js";
import {
  createChunkStream,
  ingestStreamingChunk,
  StreamingQueue,
  type StreamChunk,
  type StreamingClientInternals,
} from "./streaming.js";
import {
  ExponentialBackoff,
  ReconnectController,
  type BackoffPolicy,
} from "./reconnect.js";

export {
  ChunkReassembler,
  crc32,
  decodeChunkInfo,
  encodeChunkInfo,
  emptyChunkInfo,
  isChunkedFrame,
  verifyChecksum,
  type ChunkInfo,
} from "./chunking.js";

export {
  StreamingQueue,
  MAX_QUEUE_SIZE,
  type StreamChunk,
} from "./streaming.js";

export {
  ExponentialBackoff,
  ReconnectController,
  type BackoffPolicy,
  type ExponentialBackoffOptions,
  type ReconnectControllerOptions,
  type ReconnectAttemptFn,
  type ReconnectState,
} from "./reconnect.js";

/**
 * Default upper bound for the declared `total_bytes` of a chunked response
 * (64 MB). A chunked frame whose header claims more than this limit rejects
 * the pending request instead of allocating the reassembly buffer, so a
 * corrupt or malicious header can neither exhaust memory nor throw a
 * RangeError out of the WebSocket message handler. Override per client via
 * `BifrostClientOptions.maxChunkedResponseBytes`.
 */
export const DEFAULT_MAX_CHUNKED_RESPONSE_BYTES = 64 * 1024 * 1024;

/**
 * Largest single WebSocket message the server accepts (4 MiB, `MaxFrameSize`
 * in BifrostBinaryMiddleware). The server answers a bigger message by closing
 * the whole connection with a protocol error, so a request frame over this
 * goes out as `REQUEST_CHUNK_BYTES` Chunk frames instead.
 */
export const MAX_REQUEST_FRAME_BYTES = 4 * 1024 * 1024;

/**
 * Largest request the server reassembles from Chunk frames (64 MiB,
 * `ChunkReceiver.DefaultMaxReassemblyBytes`). The client refuses a bigger
 * request before sending any of it.
 */
export const MAX_REQUEST_BYTES = 64 * 1024 * 1024;

/**
 * Payload size of each Chunk frame of a request over `MAX_REQUEST_FRAME_BYTES`.
 * Well under the frame limit once the envelope is added; a 64 MiB request is
 * 64 frames, far inside the server's 65536-chunk cap.
 */
const REQUEST_CHUNK_BYTES = 1024 * 1024;

/**
 * Most requests the server runs at once on one connection
 * (`BifrostBinaryMiddleware.MaxConcurrentRequestsPerConnection`). It refuses,
 * rather than queues, a request past the cap, so the client holds the rest
 * back in a FIFO queue and sends one each time a slot frees.
 */
export const MAX_CONCURRENT_REQUESTS_PER_CONNECTION = 16;

/**
 * Machine-readable codes the server puts in an Error frame's `errorCode`
 * (wire field 13; `BinaryErrorCode` in BifrostQL.Server). Branch on these,
 * never on the frame's text. An Error frame without a code carries an empty
 * string.
 */
export const BinaryErrorCode = {
  /**
   * The connection already runs MAX_CONCURRENT_REQUESTS_PER_CONNECTION
   * requests. The request never ran, so the client puts it back at the head of
   * its queue instead of reporting it.
   */
  TooManyConcurrentRequests: "TOO_MANY_CONCURRENT_REQUESTS",
  /** The request_id names a request still in flight on the connection. */
  RequestIdInFlight: "REQUEST_ID_IN_FLIGHT",
  /**
   * The mount states an ack timeout and this client did not ack the next
   * chunk of the response within it. Only that response was abandoned; the
   * request ran, so resending re-runs it.
   */
  ChunkAckTimeout: "CHUNK_ACK_TIMEOUT",
} as const;

/** Protobuf field number of `error_code` on BifrostMessage. */
const ERROR_CODE_FIELD = 13;

/**
 * Delay before resending a refused request when the client has no request of
 * its own on the wire. The server releases a slot only after the response's
 * last frame (and, for a chunked one, the last ack) has been handled, so the
 * client can see a response end slightly before the server frees its slot.
 * Not measured: a short pause that keeps a lone refused request from
 * resending in a tight loop.
 */
const CONCURRENCY_REFUSAL_RETRY_MS = 10;

/** WebSocket close code 1002, which the server sends after a frame it could not process. */
const PROTOCOL_ERROR_CLOSE_CODE = 1002;

/**
 * Builds the rejection for a request over `MAX_REQUEST_BYTES`. Shared by the
 * unary and streaming send paths.
 */
export function oversizedRequestError(frameBytes: number): Error {
  return new Error(
    `Request is ${frameBytes} bytes, over the server's ${MAX_REQUEST_BYTES}-byte reassembly limit; it was not sent.`
  );
}

/**
 * Minimal WebSocket interface consumed by BifrostBinaryClient.
 * Compatible with the browser WebSocket API and Node.js ws/undici WebSocket.
 */
interface IWebSocket {
  readonly readyState: number;
  binaryType: string;
  onopen: ((ev: unknown) => void) | null;
  onclose: ((ev: { code: number; reason: string }) => void) | null;
  onerror: ((ev: unknown) => void) | null;
  onmessage: ((ev: { data: unknown }) => void) | null;
  send(data: Uint8Array | ArrayBuffer): void;
  close(code?: number, reason?: string): void;
}

/**
 * Optional per-connection options passed to the WebSocket constructor. Only
 * `headers` is used today; it maps to the `ws`/undici Node WebSocket options
 * bag (`new WebSocket(url, undefined, { headers })`). Browser WebSocket ignores
 * the extra constructor argument, so passing this is a no-op there.
 */
interface IWebSocketOptions {
  headers?: Record<string, string>;
}

interface IWebSocketConstructor {
  new (
    url: string,
    protocols?: string | string[],
    options?: IWebSocketOptions
  ): IWebSocket;
  readonly OPEN: number;
}

/**
 * Message types matching the server-side BifrostMessageType enum.
 */
export const enum BifrostMessageType {
  Query = 0,
  Mutation = 1,
  Result = 2,
  Error = 3,
  Chunk = 4,
  ChunkAck = 5,
  Resume = 6,
  ResumeAck = 7,
  ChunkNack = 8,
}

/**
 * Binary transport envelope matching the server's BifrostMessage protobuf wire format.
 *
 * Wire format fields (see src/BifrostQL.Server/BifrostMessage.cs):
 *   field  1: request_id      (uint32, varint)
 *   field  2: type            (int32, varint)
 *   field  3: query           (string, length-delimited)
 *   field  4: variables_json  (string, length-delimited)
 *   field  5: payload         (bytes, length-delimited)
 *   field  6: errors          (repeated string, length-delimited)
 *   field  7: chunk_sequence  (uint32, varint)
 *   field  8: chunk_total     (uint32, varint)
 *   field  9: chunk_offset    (uint64, varint)
 *   field 10: total_bytes     (uint64, varint)
 *   field 11: chunk_checksum  (uint32, varint)
 *   field 12: last_sequence   (uint32, varint) — Resume only
 *   field 13: error_code      (string, length-delimited) — Error frames only
 */
export interface BifrostMessage {
  requestId: number;
  type: BifrostMessageType;
  query: string;
  variablesJson: string;
  payload: Uint8Array;
  errors: string[];
  /**
   * Chunked-frame metadata. Populated by `decodeMessage` when the frame carries
   * any chunk_* fields; otherwise all fields are zero (proto3 defaults). A
   * frame is "chunked" when chunkInfo.total > 0.
   */
  chunkInfo: ChunkInfo;
  /**
   * Wire field 12 (`last_sequence`), carried only by the server's Resume /
   * ResumeAck frames. The client never sends Resume: the server keeps sent
   * chunks per connection, so a reconnected socket has nothing to resume. The
   * field is decoded so the codec mirrors the server's message shape.
   */
  lastSequence: number;
  /**
   * Wire field 13 (`error_code`): the machine-readable code of a server Error
   * frame (see `BinaryErrorCode`), or "" when the frame carries none.
   */
  errorCode: string;
}

export function encodeMessage(msg: BifrostMessage): Uint8Array {
  const writer = new BinaryWriter();

  if (msg.requestId !== 0) {
    writer.tag(1, WireType.Varint).uint32(msg.requestId);
  }
  if (msg.type !== BifrostMessageType.Query) {
    writer.tag(2, WireType.Varint).int32(msg.type);
  }
  if (msg.query.length > 0) {
    writer.tag(3, WireType.LengthDelimited).string(msg.query);
  }
  if (msg.variablesJson.length > 0) {
    writer.tag(4, WireType.LengthDelimited).string(msg.variablesJson);
  }
  if (msg.payload.length > 0) {
    writer.tag(5, WireType.LengthDelimited).bytes(msg.payload);
  }
  for (const error of msg.errors) {
    writer.tag(6, WireType.LengthDelimited).string(error);
  }
  if (msg.chunkInfo.sequence !== 0) {
    writer
      .tag(CHUNK_FIELD.ChunkSequence, WireType.Varint)
      .uint32(msg.chunkInfo.sequence);
  }
  if (msg.chunkInfo.total !== 0) {
    writer
      .tag(CHUNK_FIELD.ChunkTotal, WireType.Varint)
      .uint32(msg.chunkInfo.total);
  }
  if (msg.chunkInfo.offset !== 0) {
    writer
      .tag(CHUNK_FIELD.ChunkOffset, WireType.Varint)
      .uint64(msg.chunkInfo.offset);
  }
  if (msg.chunkInfo.totalBytes !== 0) {
    writer
      .tag(CHUNK_FIELD.TotalBytes, WireType.Varint)
      .uint64(msg.chunkInfo.totalBytes);
  }
  if (msg.chunkInfo.checksum !== 0) {
    writer
      .tag(CHUNK_FIELD.ChunkChecksum, WireType.Varint)
      .uint32(msg.chunkInfo.checksum);
  }
  if (msg.lastSequence !== 0) {
    writer.tag(CHUNK_FIELD.LastSequence, WireType.Varint).uint32(msg.lastSequence);
  }
  if (msg.errorCode.length > 0) {
    writer.tag(ERROR_CODE_FIELD, WireType.LengthDelimited).string(msg.errorCode);
  }

  return writer.finish();
}

export function decodeMessage(data: Uint8Array): BifrostMessage {
  const reader = new BinaryReader(data);
  const msg: BifrostMessage = {
    requestId: 0,
    type: BifrostMessageType.Query,
    query: "",
    variablesJson: "",
    payload: new Uint8Array(0),
    errors: [],
    chunkInfo: emptyChunkInfo(),
    lastSequence: 0,
    errorCode: "",
  };

  while (reader.pos < reader.len) {
    const [fieldNo, wireType] = reader.tag();
    switch (fieldNo) {
      case 1:
        msg.requestId = reader.uint32();
        break;
      case 2:
        msg.type = reader.int32() as BifrostMessageType;
        break;
      case 3:
        msg.query = reader.string();
        break;
      case 4:
        msg.variablesJson = reader.string();
        break;
      case 5:
        msg.payload = reader.bytes();
        break;
      case 6:
        msg.errors.push(reader.string());
        break;
      case CHUNK_FIELD.ChunkSequence:
        msg.chunkInfo.sequence = reader.uint32();
        break;
      case CHUNK_FIELD.ChunkTotal:
        msg.chunkInfo.total = reader.uint32();
        break;
      case CHUNK_FIELD.ChunkOffset:
        msg.chunkInfo.offset = uint64FieldToNumber(reader.uint64());
        break;
      case CHUNK_FIELD.TotalBytes:
        msg.chunkInfo.totalBytes = uint64FieldToNumber(reader.uint64());
        break;
      case CHUNK_FIELD.ChunkChecksum:
        msg.chunkInfo.checksum = reader.uint32();
        break;
      case CHUNK_FIELD.LastSequence:
        msg.lastSequence = reader.uint32();
        break;
      case ERROR_CODE_FIELD:
        msg.errorCode = reader.string();
        break;
      default:
        reader.skip(wireType);
        break;
    }
  }

  return msg;
}

/**
 * Narrows a uint64 wire value to a JS number, throwing if it exceeds the safe
 * integer range. Chunk offsets and total payload sizes never approach 2^53 in
 * practice (that's 9 PB), so this is purely a defensive guard.
 */
function uint64FieldToNumber(value: bigint | string): number {
  const big = typeof value === "string" ? BigInt(value) : value;
  if (big > BigInt(Number.MAX_SAFE_INTEGER)) {
    throw new Error(
      `uint64 chunk field ${big} exceeds Number.MAX_SAFE_INTEGER`
    );
  }
  return Number(big);
}

/**
 * Options for configuring the BifrostBinaryClient.
 */
export interface BifrostClientOptions {
  /** WebSocket endpoint URL (e.g., "ws://localhost:5000/bifrost-ws"). */
  url: string;
  /**
   * Extra headers to send with the WebSocket upgrade request (e.g. an
   * `Authorization` bearer token). Forwarded to the WebSocket constructor's
   * options bag. Honored by Node WebSocket implementations (`ws`, undici's
   * global `WebSocket`); the browser WebSocket API cannot set custom handshake
   * headers, so these are ignored there.
   */
  headers?: Record<string, string>;
  /** Timeout in milliseconds for individual requests. Defaults to 30000. */
  requestTimeoutMs?: number;
  /**
   * WebSocket constructor to use. Defaults to the global WebSocket.
   * Pass a custom constructor for Node.js environments without a global WebSocket
   * (e.g., the `ws` package: `import WebSocket from "ws"`).
   */
  WebSocket?: IWebSocketConstructor;
  /** Called when the connection is established. */
  onOpen?: () => void;
  /** Called when the connection is closed. */
  onClose?: (code: number, reason: string) => void;
  /** Called on connection or protocol errors. */
  onError?: (error: Error) => void;
  /**
   * Called once per received chunk while a chunked response is being
   * reassembled. `received` is the cumulative chunk count for the request,
   * `total` is the declared chunk_total. Useful for upload/download progress
   * UIs. Not called for single-frame (non-chunked) responses.
   */
  onChunkProgress?: (
    requestId: number,
    received: number,
    total: number
  ) => void;

  /**
   * Upper bound in bytes for the declared `total_bytes` of a chunked
   * response. A chunked frame claiming more than this rejects the pending
   * request before any buffer is allocated. Defaults to
   * `DEFAULT_MAX_CHUNKED_RESPONSE_BYTES` (64 MB).
   */
  maxChunkedResponseBytes?: number;

  /**
   * Whether to attempt automatic reconnection after an abnormal close.
   * Defaults to `true`. A normal close (code 1000) or an explicit
   * `client.close()` call never reconnects regardless of this setting.
   */
  autoReconnect?: boolean;

  /**
   * Maximum reconnect attempts before giving up and rejecting all pending
   * requests with the most recent connect error. Defaults to `Infinity`.
   */
  maxReconnectAttempts?: number;

  /**
   * Backoff policy used to compute the delay before each reconnect attempt.
   * Defaults to a fresh `ExponentialBackoff` instance with the standard 100ms
   * → 30s schedule and 25% jitter. Pass a deterministic policy in tests.
   */
  backoff?: BackoffPolicy;

  /**
   * Called after a successful reconnect, with the attempt number that
   * succeeded. Useful for UI indicators and metrics.
   */
  onReconnect?: (attempt: number) => void;

  /**
   * Called when reconnect attempts are exhausted (`maxReconnectAttempts`
   * reached). The client transitions to a closed state and all pending
   * requests are rejected with `error`.
   */
  onReconnectFailed?: (attempts: number, error: Error) => void;
}

/**
 * The request as it goes on the wire, captured once at call time. A request
 * waiting for a concurrency slot is sent from it, and a query is re-sent from
 * it after a reconnect (the server loses every request, and every buffered
 * chunk, with the connection). Stored on both `pending` and `streaming`
 * entries.
 */
export interface RequestMetadata {
  type: BifrostMessageType;
  /** The encoded Query/Mutation frame. */
  frame: Uint8Array;
}

interface PendingRequest {
  resolve: (result: BifrostQueryResult) => void;
  reject: (error: Error) => void;
  timer: ReturnType<typeof setTimeout>;
  meta: RequestMetadata;
}

/**
 * All per-request state for one in-flight unary (non-streaming) query or
 * mutation, consolidated into a single object so the promise settlers and the
 * chunk reassembler live together instead of in parallel Maps keyed by
 * requestId.
 */
interface UnaryRequestState {
  /** Promise settlers, timeout handle, and replay metadata for the request. */
  pending: PendingRequest;
  /**
   * Chunk reassembler, present only while a chunked response for this request
   * is being reassembled. Absent for single-frame responses.
   */
  reassembler?: ChunkReassembler;
}

/**
 * All per-request state for one active streaming request, consolidated into a
 * single object so the streaming queue, its replay metadata and its idle
 * timer live together instead of in parallel Maps keyed by requestId.
 */
interface StreamingState {
  /** Queue chunks route into; removes itself via its cleanup callback. */
  queue: StreamingQueue;
  /**
   * Original request metadata so the reconnect path can replay the initiating
   * frame. Kept here rather than on the StreamingQueue so streaming.ts stays
   * agnostic of reconnect concerns.
   */
  meta: RequestMetadata;
  /**
   * Idle-timeout handle. Armed when the queue is registered and reset on every
   * accepted chunk, so a stream whose server goes silent for `requestTimeoutMs`
   * fails its iterator instead of awaiting forever. Undefined while unarmed
   * (during a disconnect, before replay re-arms it).
   */
  timer?: ReturnType<typeof setTimeout>;
}

/**
 * Result of a BifrostQL query or mutation.
 */
export interface BifrostQueryResult {
  /**
   * The GraphQL `data` payload (the response envelope is unwrapped by the
   * client), or null if the response contained no payload.
   */
  data: unknown;
  /** Error messages from the server, empty array on success. */
  errors: string[];
}

/**
 * Builds the rejection used when a request's `AbortSignal` fires. Uses a
 * DOMException with name "AbortError" when available (matching `fetch`), so
 * callers that special-case aborts see the same shape across transports; falls
 * back to a plain Error in environments without DOMException.
 */
function abortError(): Error {
  if (typeof DOMException === "function") {
    return new DOMException("The operation was aborted.", "AbortError");
  }
  const err = new Error("The operation was aborted.");
  err.name = "AbortError";
  return err;
}

/**
 * WebSocket client for BifrostQL binary protobuf transport.
 *
 * Connects to the BifrostQL WebSocket binary endpoint, encodes requests as
 * protobuf binary frames, and decodes responses. Supports connection multiplexing
 * via request_id so multiple in-flight queries can share a single connection.
 *
 * @example
 * ```ts
 * const client = new BifrostBinaryClient({ url: "ws://localhost:5000/bifrost-ws" });
 * await client.connect();
 *
 * const result = await client.query("{ users { id name } }");
 * console.log(result.data);
 *
 * client.close();
 * ```
 */
export class BifrostBinaryClient implements StreamingClientInternals {
  private ws: IWebSocket | null = null;
  private nextRequestId = 1;
  /**
   * Per-request unary state (promise settlers, chunk reassembler) for each
   * in-flight non-streaming request, one object per id.
   */
  private readonly unary = new Map<number, UnaryRequestState>();
  /**
   * Per-request streaming state (queue, replay metadata, idle timer) for each
   * active streaming request, one object per id.
   */
  private readonly streaming = new Map<number, StreamingState>();
  /**
   * Ids whose request the server is running on this socket: sent, and the
   * response's last frame not yet seen. An aborted or timed-out request keeps
   * its slot until then, because the server keeps running it. Never larger
   * than MAX_CONCURRENT_REQUESTS_PER_CONNECTION.
   */
  private readonly wireSlots = new Set<number>();
  /**
   * Ids waiting for a wire slot, oldest first. An entry whose request has
   * since settled (abort, timeout) is skipped when it reaches the head.
   */
  private readonly sendQueue: number[] = [];
  /** Pending retry of a refused request; see CONCURRENCY_REFUSAL_RETRY_MS. */
  private refusalRetryTimer: ReturnType<typeof setTimeout> | null = null;
  /**
   * Ids that were on the wire when the socket dropped. The reconnect replay
   * treats them as possibly executed (mutations rejected, started streams
   * failed); requests still waiting in `sendQueue` never reached the server.
   */
  private interruptedOnWire = new Set<number>();
  private readonly wsConstructor: IWebSocketConstructor;
  private readonly url: string;
  private readonly headers?: Record<string, string>;
  private readonly requestTimeoutMs: number;
  private readonly onOpen?: () => void;
  private readonly onClose?: (code: number, reason: string) => void;
  private readonly onError?: (error: Error) => void;
  private readonly onChunkProgress?: (
    requestId: number,
    received: number,
    total: number
  ) => void;
  private readonly maxChunkedResponseBytes: number;
  private readonly autoReconnect: boolean;
  private readonly onReconnect?: (attempt: number) => void;
  private readonly onReconnectFailed?: (attempts: number, error: Error) => void;
  private readonly reconnectController: ReconnectController | null;
  /** True once `close()` has been called, to make it permanent. */
  private closed = false;
  /**
   * The in-flight socket-open promise, shared by every concurrent `connect()`
   * and by the reconnect controller's attempts. Deduping through it prevents a
   * second overlapping `openSocket()` from creating an orphaned WebSocket whose
   * handlers overwrite the first — the socket-leak footgun. Cleared when the
   * open settles (resolve or reject).
   */
  private connectPromise: Promise<void> | null = null;

  /**
   * True while the client is in the middle of an auto-reconnect cycle. Derived
   * from the reconnect controller's state machine rather than mirrored in a
   * separate latch: the controller is mid-cycle exactly when it is waiting for
   * a backoff timer or has a connect attempt in flight. Used to keep the
   * reconnect cycle alive when a close event fires with no pending requests,
   * and to suppress the `onClose` callback for transient drops (we only fire
   * it after a clean close or an exhausted retry budget).
   */
  private get reconnecting(): boolean {
    const state = this.reconnectController?.currentState;
    return state === "waiting" || state === "connecting";
  }

  constructor(options: BifrostClientOptions) {
    this.url = options.url;
    this.headers =
      options.headers && Object.keys(options.headers).length > 0
        ? options.headers
        : undefined;
    this.requestTimeoutMs = options.requestTimeoutMs ?? 30_000;
    this.onOpen = options.onOpen;
    this.onClose = options.onClose;
    this.onError = options.onError;
    this.onChunkProgress = options.onChunkProgress;
    this.maxChunkedResponseBytes =
      options.maxChunkedResponseBytes ?? DEFAULT_MAX_CHUNKED_RESPONSE_BYTES;
    this.autoReconnect = options.autoReconnect ?? true;
    this.onReconnect = options.onReconnect;
    this.onReconnectFailed = options.onReconnectFailed;

    const WsCtor =
      options.WebSocket ??
      (globalThis as unknown as { WebSocket?: IWebSocketConstructor }).WebSocket;
    if (!WsCtor) {
      throw new Error(
        "No WebSocket implementation available. " +
          "Pass a WebSocket constructor via options.WebSocket or use a runtime with a global WebSocket."
      );
    }
    this.wsConstructor = WsCtor;

    this.reconnectController = this.autoReconnect
      ? new ReconnectController({
          policy: options.backoff ?? new ExponentialBackoff(),
          maxAttempts: options.maxReconnectAttempts ?? Number.POSITIVE_INFINITY,
          connect: () => this.openSocket(),
          onSuccess: (attempt) => this.handleReconnectSuccess(attempt),
          onGiveUp: (attempts, err) => this.handleReconnectGiveUp(attempts, err),
        })
      : null;
  }

  /**
   * Opens the WebSocket connection. Resolves when the connection is established,
   * rejects if the connection fails. Calling `connect()` while the client is
   * already in the middle of an auto-reconnect cycle is a no-op that resolves
   * once the in-flight reconnect succeeds (or rejects if it gives up).
   */
  connect(): Promise<void> {
    if (this.closed) {
      return Promise.reject(new Error("Client is closed"));
    }
    if (this.ws && this.ws.readyState === this.wsConstructor.OPEN) {
      return Promise.resolve();
    }
    // A manual reconnect after the retry budget was exhausted re-arms the
    // controller so a future drop auto-reconnects again (gave-up → idle).
    // No other state bookkeeping is needed here: `reconnecting` is derived
    // from the controller, so an active waiting/connecting cycle stays intact.
    this.reconnectController?.rearm();
    if (this.reconnectController?.currentState === "waiting") {
      // Complete the in-flight reconnect cycle now instead of opening an
      // independent socket beside it. Routing through the controller makes
      // its success path run immediately — pending requests from the drop are
      // replayed/rejected now rather than sitting until the backoff timer
      // fires (possibly timing out on a live socket) — and consumes the
      // timer, which otherwise fires later, short-circuits on the OPEN
      // socket, and "replays" healthy in-flight traffic.
      this.reconnectController.attemptNow();
    }
    return this.openSocket();
  }

  /**
   * Internal: opens a fresh WebSocket and wires up handlers. Used by both the
   * public `connect()` and by `ReconnectController` for retry attempts. The
   * returned promise mirrors the standard `connect()` semantics: resolves on
   * `onopen`, rejects on `onerror` (when no connection has been established
   * yet) or on a close event that fires before the socket ever opened.
   */
  private openSocket(): Promise<void> {
    // Already open: short-circuit instead of spawning a duplicate socket. This
    // covers the manual-connect-during-backoff race — a user calls connect()
    // while the controller is waiting, the socket opens (settling and clearing
    // `connectPromise`), and then the backoff timer fires. Without this check
    // the controller's attempt would open socket B whose onopen clobbers
    // `this.ws` while socket A is never closed (its handlers stay live).
    if (this.ws && this.ws.readyState === this.wsConstructor.OPEN) {
      return Promise.resolve();
    }
    // Dedupe overlapping opens: return the in-flight attempt rather than
    // spawning a second socket that would leak (its handlers would clobber the
    // first socket's, and the first would never be closed).
    if (this.connectPromise) {
      return this.connectPromise;
    }

    let guarded: Promise<void>;
    const attempt = new Promise<void>((resolve, reject) => {
      if (this.closed) {
        reject(new Error("Client is closed"));
        return;
      }

      const ws = this.headers
        ? new this.wsConstructor(this.url, undefined, { headers: this.headers })
        : new this.wsConstructor(this.url);
      ws.binaryType = "arraybuffer";
      let settled = false;

      ws.onopen = () => {
        // close() may have run while this socket was still connecting. Adopting
        // it now would revive a closed client (a zombie connection); discard it.
        if (this.closed) {
          try {
            ws.close(1000, "Client closed");
          } catch {
            /* ignore */
          }
          if (!settled) {
            settled = true;
            reject(new Error("Client is closed"));
          }
          return;
        }
        this.ws = ws;
        this.onOpen?.();
        if (!settled) {
          settled = true;
          resolve();
        }
      };

      ws.onerror = () => {
        const err = new Error("WebSocket connection failed");
        this.onError?.(err);
        if (!settled) {
          // Only reject the connect promise if the socket never opened. If we
          // already settled and the error fires later, route it through the
          // close handler instead so the reconnect path picks it up.
          settled = true;
          reject(err);
        }
      };

      ws.onclose = (event) => {
        // The socket may have closed before ever opening (failed handshake);
        // surface that as a rejection of the in-flight connect promise so the
        // reconnect controller can schedule its next attempt with the close
        // info as the failure cause.
        if (!settled) {
          settled = true;
          reject(
            new Error(`Connection closed: ${event.code} ${event.reason}`)
          );
        }
        this.handleSocketClose(ws, event.code, event.reason);
      };

      ws.onmessage = (event) => {
        // Nothing may throw out of the raw message listener: on Node's `ws`
        // an exception here escapes as an uncaught 'message' listener error
        // and can crash the process, killing dispatch for every request.
        try {
          this.handleMessage(event.data as ArrayBuffer);
        } catch (err) {
          this.onError?.(err instanceof Error ? err : new Error(String(err)));
        }
      };
    });

    guarded = attempt.finally(() => {
      if (this.connectPromise === guarded) {
        this.connectPromise = null;
      }
    });
    this.connectPromise = guarded;
    return guarded;
  }

  /**
   * Decides whether a socket close should trigger reconnect or terminate the
   * client. Called once per closed socket. Normal closes (1000) and explicit
   * `client.close()` calls always terminate; abnormal closes route through the
   * reconnect controller when `autoReconnect` is enabled.
   */
  private handleSocketClose(
    closedWs: IWebSocket,
    code: number,
    reason: string
  ): void {
    // If a different socket is already attached (e.g. during a fast retry),
    // ignore the late close from the previous instance.
    if (this.ws !== null && this.ws !== closedWs) {
      return;
    }
    this.ws = null;
    // A disconnect ends any server-side streaming for aborted requests (the
    // server loses the socket), so the tombstones guarding against their late
    // chunks can never match again — clear them to prevent unbounded growth.
    this.abortedRequests.clear();

    // Any abnormal close reconnects when autoReconnect is on — including a drop
    // on an otherwise idle connection (no pending/streaming requests). Gating on
    // in-flight work meant an idle socket that dropped stayed dead until the
    // next request, defeating autoReconnect's promise of a warm connection.
    const shouldReconnect =
      !this.closed &&
      this.autoReconnect &&
      this.reconnectController !== null &&
      code !== 1000;

    if (shouldReconnect && this.reconnectController) {
      if (code === PROTOCOL_ERROR_CLOSE_CODE) {
        // The server closes with 1002 after a frame it could not process (an
        // oversized or malformed message, or a request that faulted). It does
        // not say which request caused it, and replaying them all would send
        // the offending one again, close again, and loop. Fail every in-flight
        // request instead; the socket itself still reconnects.
        this.rejectAllPending(
          new Error(
            `Connection closed by the server for a protocol error (${code} ${reason}); in-flight requests were not replayed.`
          )
        );
      }
      // The server dropped every request it was running. Remember which ones
      // reached it (the replay must treat those as possibly executed), and
      // put them back at the head of the queue, ahead of requests that never
      // left the client, so they go out first on the new socket.
      this.interruptedOnWire = new Set(this.wireSlots);
      this.sendQueue.unshift(...this.wireSlots);
      this.wireSlots.clear();
      this.cancelRefusalRetry();
      // The socket is gone, so streaming idle timers would fire against a dead
      // connection during the backoff window and prematurely fail iterators
      // that are about to be re-sent. Clear them here; replayPendingAfterReconnect
      // re-arms one for every streaming request that survives it.
      this.clearAllStreamingTimeouts();
      // The server's chunk buffer dies with the connection, so a partial
      // reassembly can never be completed on the next socket. Free it now
      // instead of holding up to maxChunkedResponseBytes through the backoff.
      this.dropPartialReassemblies();
      this.reconnectController.start(
        new Error(`Connection closed: ${code} ${reason}`)
      );
      return;
    }

    // Normal close path: reject everything, clear state, fire onClose.
    this.rejectAllPending(new Error(`Connection closed: ${code} ${reason}`));
    if (this.reconnectController && !this.closed) {
      this.reconnectController.cancel();
    }
    this.onClose?.(code, reason);
  }

  /**
   * Drops every partial chunk reassembly after a disconnect. The server keeps
   * sent chunks per connection (its `ChunkBuffer` lives and dies with the
   * socket), so a fresh socket can never deliver the missing tail: a query is
   * re-run from the start and a mutation is rejected. Streams forget the acks
   * they still owe: those chunks belong to the dead connection, and an ack on
   * the next socket would name a transfer that socket never started.
   */
  private dropPartialReassemblies(): void {
    for (const state of this.unary.values()) {
      state.reassembler = undefined;
    }
    for (const state of this.streaming.values()) {
      state.queue.forgetOwedAcks();
    }
  }

  /**
   * Tombstones for requests aborted client-side while the server may still be
   * streaming their chunked response. The protocol has no cancel frame, so the
   * server keeps sending; without a tombstone every late chunk would surface
   * as a spurious `onError("Received chunk for unknown requestId ...")`. A
   * tombstone is dropped once the final chunk of the response passes through
   * (or wholesale on disconnect/close, when the server-side stream dies).
   */
  private readonly abortedRequests = new Set<number>();

  private handleReconnectSuccess(attempt: number): void {
    this.onReconnect?.(attempt);
    this.replayPendingAfterReconnect();
  }

  private handleReconnectGiveUp(attempts: number, lastError: Error): void {
    const err = new Error(
      `Reconnect failed after ${attempts} attempts: ${lastError.message}`
    );
    this.rejectAllPending(err);
    this.onReconnectFailed?.(attempts, lastError);
    this.onClose?.(1006, lastError.message);
  }

  /**
   * Walks every request that was on the wire when the socket dropped. The
   * server lost them all with the old socket, buffered chunks included, so
   * nothing can be resumed:
   * - a query is re-sent from the start (its partial reassembly was dropped
   *   at the disconnect, and the caller has seen nothing yet);
   * - a mutation is rejected, because it may already have committed and a
   *   re-send risks applying it twice (at-most-once);
   * - a stream whose consumer already received chunks is failed, because a
   *   re-run would deliver them again.
   * Requests still waiting for a slot never reached the server and are sent
   * normally. Called from the reconnect controller's success callback once a
   * fresh socket is open.
   */
  private replayPendingAfterReconnect(): void {
    if (!this.ws || this.ws.readyState !== this.wsConstructor.OPEN) {
      return;
    }
    const interrupted = this.interruptedOnWire;
    this.interruptedOnWire = new Set();

    // Snapshot entries first: the rejection branches mutate `unary` and
    // `streaming` while we iterate.
    for (const [requestId, state] of Array.from(this.unary)) {
      if (!interrupted.has(requestId)) {
        continue;
      }
      if (state.pending.meta.type === BifrostMessageType.Mutation) {
        this.rejectPendingRequest(
          requestId,
          new Error(
            `Mutation for request ${requestId} was interrupted by a reconnect and was not automatically retried to avoid double execution.`
          )
        );
        continue;
      }
      // The request-timeout clock was set once at send-time and has been
      // ticking against the dead socket; restart it so the replayed request
      // gets a full window on the fresh connection.
      this.rearmRequestTimeout(requestId);
    }

    for (const [requestId, state] of Array.from(this.streaming)) {
      if (!interrupted.has(requestId)) {
        this.armStreamingTimeout(requestId);
        continue;
      }
      // queue.error runs the cleanup callback → removeStreamingQueue.
      if (state.queue.receivedAnyChunk) {
        state.queue.error(
          new Error(
            `Streaming request ${requestId} was interrupted by a reconnect after it had delivered chunks; it was not restarted because a re-run would deliver them again.`
          )
        );
        continue;
      }
      if (state.meta.type === BifrostMessageType.Mutation) {
        state.queue.error(
          new Error(
            `Streaming mutation for request ${requestId} was interrupted by a reconnect and was not automatically retried to avoid double execution.`
          )
        );
        continue;
      }
      this.armStreamingTimeout(requestId);
    }

    // Survivors sit at the head of the queue (moved there at the disconnect);
    // the requests just failed are skipped when they reach it.
    this.pumpSendQueue();
  }

  /**
   * Sends queued requests while the server has free slots. Called whenever a
   * request is queued, a slot frees, or a fresh socket opens.
   */
  private pumpSendQueue(): void {
    while (
      this.wireSlots.size < MAX_CONCURRENT_REQUESTS_PER_CONNECTION &&
      this.sendQueue.length > 0
    ) {
      if (!this.ws || this.ws.readyState !== this.wsConstructor.OPEN) {
        return;
      }
      const requestId = this.sendQueue.shift()!;
      const meta =
        this.unary.get(requestId)?.pending.meta ??
        this.streaming.get(requestId)?.meta;
      if (!meta) {
        // Settled while it waited (abort, timeout, consumer break).
        continue;
      }
      this.wireSlots.add(requestId);
      try {
        this.sendRequestFrame(requestId, meta.frame);
      } catch (err) {
        // A synchronous throw (e.g. the socket dropped between the readyState
        // check and send()) means the request never reached the wire.
        this.wireSlots.delete(requestId);
        this.failUnsentRequest(
          requestId,
          err instanceof Error ? err : new Error(String(err))
        );
      }
    }
  }

  /**
   * Puts an encoded request on the wire: whole when it fits one server frame,
   * otherwise as Chunk frames carrying slices of it, which the server
   * reassembles (ChunkReceiver) and runs as if it had arrived whole. The
   * slices go out back to back in one synchronous loop, so no other frame of
   * this client lands between them and the server holds at most one partial
   * request per connection, inside its per-connection reassembly cap. The
   * server acks each slice; nothing waits for those acks (handleMessage drops
   * them). Callers have already refused a request over MAX_REQUEST_BYTES.
   */
  private sendRequestFrame(requestId: number, frame: Uint8Array): void {
    const ws = this.ws!;
    if (frame.length <= MAX_REQUEST_FRAME_BYTES) {
      ws.send(frame);
      return;
    }
    const total = Math.ceil(frame.length / REQUEST_CHUNK_BYTES);
    for (let sequence = 0; sequence < total; sequence++) {
      const offset = sequence * REQUEST_CHUNK_BYTES;
      const slice = frame.subarray(offset, offset + REQUEST_CHUNK_BYTES);
      ws.send(
        encodeMessage(
          this.makeFrame({
            requestId,
            type: BifrostMessageType.Chunk,
            payload: slice,
            chunkInfo: {
              sequence,
              total,
              offset,
              totalBytes: frame.length,
              checksum: crc32(slice),
            },
          })
        )
      );
    }
  }

  /** Fails a request that never reached the server, unary or streaming. */
  private failUnsentRequest(requestId: number, error: Error): void {
    if (this.unary.has(requestId)) {
      this.rejectPendingRequest(requestId, error);
      return;
    }
    this.streaming.get(requestId)?.queue.error(error);
  }

  /**
   * Frees the wire slot of a request whose response has ended (its last frame
   * arrived), and sends the next queued request.
   */
  private releaseWireSlot(requestId: number): void {
    if (this.wireSlots.delete(requestId)) {
      this.pumpSendQueue();
    }
  }

  /**
   * The server refused a request because its own count of running requests
   * had not yet dropped below the cap. The request never ran, so it goes back
   * to the head of the queue. It is resent when another of this client's
   * requests ends, or after CONCURRENCY_REFUSAL_RETRY_MS when none is on the
   * wire.
   */
  private requeueRefusedRequest(requestId: number): void {
    this.wireSlots.delete(requestId);
    this.sendQueue.unshift(requestId);
    if (this.wireSlots.size === 0 && this.refusalRetryTimer === null) {
      this.refusalRetryTimer = setTimeout(() => {
        this.refusalRetryTimer = null;
        this.pumpSendQueue();
      }, CONCURRENCY_REFUSAL_RETRY_MS);
    }
  }

  private cancelRefusalRetry(): void {
    if (this.refusalRetryTimer !== null) {
      clearTimeout(this.refusalRetryTimer);
      this.refusalRetryTimer = null;
    }
  }

  /**
   * Fills a partial frame with the protobuf proto3 defaults so callers only
   * spell out the fields that differ. Mirrors the zero-values `decodeMessage`
   * initializes: empty strings, empty payload/errors, empty chunkInfo, and a
   * zero `lastSequence`.
   */
  private makeFrame(
    partial: Partial<BifrostMessage> &
      Pick<BifrostMessage, "requestId" | "type">
  ): BifrostMessage {
    return {
      requestId: partial.requestId,
      type: partial.type,
      query: partial.query ?? "",
      variablesJson: partial.variablesJson ?? "",
      payload: partial.payload ?? new Uint8Array(0),
      errors: partial.errors ?? [],
      chunkInfo: partial.chunkInfo ?? emptyChunkInfo(),
      lastSequence: partial.lastSequence ?? 0,
      errorCode: partial.errorCode ?? "",
    };
  }

  /**
   * Encodes and sends a frame only if the socket is open, silently dropping it
   * otherwise. Request frames go through `pumpSendQueue`, which must unwind a
   * request whose send fails, so they do their own guard instead.
   */
  private trySend(frame: BifrostMessage): void {
    if (!this.ws || this.ws.readyState !== this.wsConstructor.OPEN) {
      return;
    }
    this.ws.send(encodeMessage(frame));
  }

  /**
   * Sends a GraphQL query over the binary transport.
   * Returns a promise that resolves when the server responds with the matching request_id.
   */
  query(
    queryText: string,
    variables?: Record<string, unknown>,
    signal?: AbortSignal
  ): Promise<BifrostQueryResult> {
    return this.send(BifrostMessageType.Query, queryText, variables, signal);
  }

  /**
   * Sends a GraphQL mutation over the binary transport.
   * Returns a promise that resolves when the server responds with the matching request_id.
   */
  mutate(
    mutationText: string,
    variables?: Record<string, unknown>,
    signal?: AbortSignal
  ): Promise<BifrostQueryResult> {
    return this.send(BifrostMessageType.Mutation, mutationText, variables, signal);
  }

  /**
   * Sends a GraphQL query and returns an async iterator of `StreamChunk`s.
   * Each chunk is yielded as soon as it arrives and verifies CRC32, in
   * sequence order even if wire delivery was shuffled. Use this when you want
   * to process a chunked response incrementally instead of waiting for full
   * reassembly via `query()`.
   *
   * @example
   * ```ts
   * for await (const chunk of client.stream("{ download { bytes } }")) {
   *   write(chunk.bytes);
   *   if (chunk.isLast) console.log("done");
   * }
   * ```
   */
  stream(
    queryText: string,
    variables?: Record<string, unknown>
  ): AsyncIterableIterator<StreamChunk> {
    return createChunkStream(this, BifrostMessageType.Query, queryText, variables);
  }

  /**
   * Streaming counterpart to `mutate()`. See `stream()` for usage.
   */
  streamMutation(
    mutationText: string,
    variables?: Record<string, unknown>
  ): AsyncIterableIterator<StreamChunk> {
    return createChunkStream(
      this,
      BifrostMessageType.Mutation,
      mutationText,
      variables
    );
  }

  // --- StreamingClientInternals (used by createChunkStream / StreamingQueue) ---

  /** @internal Allocates the next request id, wrapping at uint32 max. */
  allocateRequestId(): number {
    const id = this.nextRequestId++;
    if (this.nextRequestId > 0xffffffff) {
      this.nextRequestId = 1;
    }
    return id;
  }

  /**
   * @internal Registers a streaming queue so chunks for `requestId` route to
   * it, arms the idle timeout, and queues the request frame for a wire slot.
   * `meta` is passed by `createChunkStream` at registration time rather than
   * stashed on the client, so a failed (not-connected) stream that never
   * registers can't leave stale metadata for the next request to claim.
   */
  startStreamingRequest(
    requestId: number,
    queue: StreamingQueue,
    meta: RequestMetadata
  ): void {
    this.streaming.set(requestId, { queue, meta });
    this.armStreamingTimeout(requestId);
    this.sendQueue.push(requestId);
    this.pumpSendQueue();
  }

  /**
   * @internal Removes a streaming queue (called on stream end / consumer
   * break / error). When the stream ended before its final chunk
   * (`completed === false`), tombstone the id: the protocol has no cancel
   * frame, so the server may keep streaming on the shared socket, and without
   * a tombstone every late chunk would surface as an unknown-requestId error
   * and go un-acked — stalling the server's ack window and, with it, the whole
   * shared connection. A stream that never left the send queue needs no
   * tombstone: the server never heard of it.
   */
  removeStreamingQueue(requestId: number, completed = false): void {
    // Clear the idle timer before dropping the entry — clearStreamingTimeout
    // reads the timer off the entry, so deleting first would leak it.
    this.clearStreamingTimeout(requestId);
    this.streaming.delete(requestId);
    if (!completed && this.wireSlots.has(requestId)) {
      this.abortedRequests.add(requestId);
    }
  }

  /**
   * Arms (or re-arms) the idle timeout for a streaming request. Reset on every
   * accepted chunk so long transfers with steady progress never trip it; fires
   * only when the server goes silent for a full `requestTimeoutMs` window with
   * the stream unfinished.
   */
  /**
   * @internal Acks a streamed chunk once its consumer has taken it (or the
   * stream was torn down holding it). The server stops sending after
   * `ackWindow` unacked chunks, so this is what paces the transfer to the
   * consumer. The ack is progress, so it also restarts the idle timer.
   */
  ackStreamChunk(requestId: number, sequence: number): void {
    this.sendChunkAck(requestId, sequence);
    this.armStreamingTimeout(requestId);
  }

  private armStreamingTimeout(requestId: number): void {
    const state = this.streaming.get(requestId);
    if (!state) {
      return;
    }
    if (state.timer !== undefined) {
      clearTimeout(state.timer);
    }
    state.timer = setTimeout(() => {
      const current = this.streaming.get(requestId);
      if (current) {
        current.timer = undefined;
      }
      // The consumer still holds chunks it has not taken, so the server is
      // waiting on this client's acks: backpressure, not a dead server.
      // Re-arm instead of failing.
      if (current?.queue.awaitingConsumer) {
        this.armStreamingTimeout(requestId);
        return;
      }
      // queue.error runs the cleanup callback → removeStreamingQueue, which
      // tombstones the id so the server's later chunks are ack-and-dropped.
      current?.queue.error(
        new Error(`Streaming request ${requestId} timed out`)
      );
    }, this.requestTimeoutMs);
  }

  private clearStreamingTimeout(requestId: number): void {
    const state = this.streaming.get(requestId);
    if (state?.timer !== undefined) {
      clearTimeout(state.timer);
      state.timer = undefined;
    }
  }

  private clearAllStreamingTimeouts(): void {
    for (const state of this.streaming.values()) {
      if (state.timer !== undefined) {
        clearTimeout(state.timer);
        state.timer = undefined;
      }
    }
  }

  /** Whether the WebSocket connection is currently open. */
  get connected(): boolean {
    return this.ws?.readyState === this.wsConstructor.OPEN;
  }

  /**
   * Number of unary requests currently awaiting responses, including those
   * still waiting in the send queue for a concurrency slot.
   */
  get pendingCount(): number {
    return this.unary.size;
  }

  /** Number of requests (unary and streaming) waiting for a concurrency slot. */
  get queuedCount(): number {
    let count = 0;
    for (const id of this.sendQueue) {
      if (this.unary.has(id) || this.streaming.has(id)) count++;
    }
    return count;
  }

  /**
   * Closes the WebSocket connection gracefully. Drops any in-progress chunk
   * reassemblers so their buffers can be garbage collected, terminates any
   * active streaming iterators with a connection-closed error, and stops the
   * reconnect controller so a transient close racing with `close()` cannot
   * accidentally re-open the connection.
   */
  close(): void {
    this.closed = true;
    if (this.reconnectController) {
      this.reconnectController.stop();
    }
    // Drop the in-flight open latch: a socket still connecting is discarded by
    // the `this.closed` guard in openSocket's onopen, and its promise settles
    // via the close/error path.
    this.connectPromise = null;
    this.clearAllStreamingTimeouts();
    this.errorAllStreamingQueues(new Error("Connection closed: client closed"));
    // Reject any non-streaming pending requests too. The previous version
    // relied on the socket's onclose event to call rejectAllPending, but with
    // reconnect support we may no longer want to wait for that round-trip.
    this.rejectAllPending(new Error("Connection closed: client closed"));
    if (this.ws) {
      this.ws.close(1000, "Client closed");
      this.ws = null;
    }
  }

  /**
   * Builds the request-timeout timer for a pending request. On fire it mirrors
   * the abort path: drops any in-flight reassembly buffer and abandons the
   * request (see `abandonRequest`). The clock starts at call time, so time
   * spent waiting for a concurrency slot counts against it.
   */
  private scheduleRequestTimeout(
    requestId: number,
    reject: (error: Error) => void
  ): ReturnType<typeof setTimeout> {
    return setTimeout(() => {
      // Dropping the unary entry discards the reassembly buffer along with
      // the promise settlers in one step.
      this.unary.delete(requestId);
      this.abandonRequest(requestId);
      reject(new Error(`Request ${requestId} timed out`));
    }, this.requestTimeoutMs);
  }

  /**
   * Marks a unary request the caller gave up on (abort, timeout). If it is on
   * the wire, the server keeps running it (the protocol has no cancel frame),
   * so its id is tombstoned: the late chunks are ack-and-dropped, keeping the
   * server's send window moving instead of stalling the shared socket or
   * spraying unknown-requestId errors. It keeps its wire slot until the
   * response's last frame arrives. A request still in the send queue never
   * reached the server; it is skipped when it reaches the head.
   */
  private abandonRequest(requestId: number): void {
    if (this.wireSlots.has(requestId)) {
      this.abortedRequests.add(requestId);
    }
  }

  /**
   * Restarts the request-timeout clock for a still-pending request after a
   * reconnect re-sends it on a fresh socket. The original timer was
   * armed once at send-time and keeps counting through the disconnect, so
   * without re-arming a request that survived the drop would reject almost
   * immediately after being replayed.
   */
  private rearmRequestTimeout(requestId: number): void {
    const entry = this.unary.get(requestId)?.pending;
    if (!entry) {
      return;
    }
    clearTimeout(entry.timer);
    entry.timer = this.scheduleRequestTimeout(requestId, entry.reject);
  }

  private send(
    type: BifrostMessageType,
    queryText: string,
    variables?: Record<string, unknown>,
    signal?: AbortSignal
  ): Promise<BifrostQueryResult> {
    return new Promise((resolve, reject) => {
      // Already-aborted requests never touch the wire.
      if (signal?.aborted) {
        reject(abortError());
        return;
      }
      if (!this.ws || this.ws.readyState !== this.wsConstructor.OPEN) {
        reject(new Error("Not connected"));
        return;
      }

      const requestId = this.allocateRequestId();
      const bytes = encodeMessage(
        this.makeFrame({
          requestId,
          type,
          query: queryText,
          variablesJson: variables ? JSON.stringify(variables) : "",
        })
      );
      if (bytes.length > MAX_REQUEST_BYTES) {
        reject(oversizedRequestError(bytes.length));
        return;
      }

      // Wrap the settlers so every settle path — server response, timeout,
      // disconnect, abort — detaches the abort listener exactly once, so a
      // superseded request can't leak its listener onto a long-lived signal.
      let onAbort: (() => void) | undefined;
      const detach = () => {
        if (onAbort && signal) signal.removeEventListener("abort", onAbort);
        onAbort = undefined;
      };
      const wrappedResolve = (result: BifrostQueryResult) => {
        detach();
        resolve(result);
      };
      const wrappedReject = (error: Error) => {
        detach();
        reject(error);
      };

      const timer = this.scheduleRequestTimeout(requestId, wrappedReject);

      const meta: RequestMetadata = { type, frame: bytes };
      this.unary.set(requestId, {
        pending: {
          resolve: wrappedResolve,
          reject: wrappedReject,
          timer,
          meta,
        },
      });

      if (signal) {
        onAbort = () => {
          const entry = this.unary.get(requestId)?.pending;
          if (entry) {
            clearTimeout(entry.timer);
            // Dropping the unary entry also discards any in-flight reassembly
            // buffer, so an aborted request leaves no partial state behind.
            this.unary.delete(requestId);
          }
          this.abandonRequest(requestId);
          wrappedReject(abortError());
        };
        signal.addEventListener("abort", onAbort, { once: true });
      }

      // Goes out now when the server has a free slot, otherwise when one
      // frees. A synchronous send failure rejects through failUnsentRequest,
      // which clears the timer and entry and detaches the abort listener.
      this.sendQueue.push(requestId);
      this.pumpSendQueue();
    });
  }

  private handleMessage(buffer: ArrayBuffer): void {
    let response: BifrostMessage;
    try {
      response = decodeMessage(new Uint8Array(buffer));
    } catch (err) {
      this.onError?.(err instanceof Error ? err : new Error(String(err)));
      return;
    }

    // The server acks each Chunk frame of a request this client sent in
    // slices (sendRequestFrame). Nothing waits on those acks, and routing one
    // as a response would settle the request early and free its slot.
    if (response.type === BifrostMessageType.ChunkAck) {
      return;
    }

    const chunked = isChunkedFrame(response.chunkInfo);
    if (!chunked && this.isConcurrencyRefusal(response)) {
      this.requeueRefusedRequest(response.requestId);
      return;
    }

    this.routeResponseFrame(response, chunked);

    // A single frame, or the last chunk of a chunked response, ends the
    // server's work for this id: its slot frees for the next queued request.
    // Released after routing, so the next request goes out only once this
    // response has been delivered.
    if (
      !chunked ||
      response.chunkInfo.sequence + 1 === response.chunkInfo.total
    ) {
      this.releaseWireSlot(response.requestId);
    }
  }

  /**
   * True for the server's refusal of a request past its concurrency cap: a
   * single Error frame for a request this client has on the wire, coded
   * `TOO_MANY_CONCURRENT_REQUESTS`. The text is never consulted.
   */
  private isConcurrencyRefusal(response: BifrostMessage): boolean {
    return (
      response.type === BifrostMessageType.Error &&
      this.wireSlots.has(response.requestId) &&
      response.errorCode === BinaryErrorCode.TooManyConcurrentRequests
    );
  }

  private routeResponseFrame(response: BifrostMessage, chunked: boolean): void {
    if (chunked) {
      this.handleChunkedFrame(response);
      return;
    }

    // Non-chunked Result/Error frames addressed to a streaming request map
    // to a single-chunk delivery (or an iterator error for type=Error).
    const streamingQueue = this.streaming.get(response.requestId)?.queue;
    if (streamingQueue) {
      this.deliverStreamingNonChunked(response, streamingQueue);
      return;
    }

    this.deliverComplete(response);
  }

  /**
   * Rejects the pending request for `requestId` (if any) with `error`, and
   * drops its reassembly state. Safe to call for ids with no pending entry.
   */
  private rejectPendingRequest(requestId: number, error: Error): void {
    const state = this.unary.get(requestId);
    if (!state) {
      return;
    }
    // Dropping the entry discards its reassembly buffer along with the timer.
    clearTimeout(state.pending.timer);
    this.unary.delete(requestId);
    state.pending.reject(error);
  }

  /**
   * Acknowledges receipt of a chunk. The server's ChunkTransfer pauses after
   * `ackWindow` (default 8) unacknowledged chunks until an ack arrives, so a
   * client that never acks never receives a response larger than
   * ackWindow × chunkThreshold (~512 KB with defaults). One ChunkAck per received chunk keeps the
   * sender's window moving; the frame shape mirrors ChunkReceiver.CreateAck
   * (request_id + type + chunk_sequence).
   */
  private sendChunkAck(requestId: number, sequence: number): void {
    this.trySend(
      this.makeFrame({
        requestId,
        type: BifrostMessageType.ChunkAck,
        chunkInfo: { ...emptyChunkInfo(), sequence },
      })
    );
  }

  /**
   * Routes a chunked frame into a per-requestId reassembler, verifies the
   * checksum, and once the final chunk arrives, deserializes the assembled
   * buffer and delivers the inner Result message to the pending promise.
   *
   * Chunks for unknown requestIds are forwarded to onError (matching how the
   * existing single-frame path silently ignores unknown responses, but with a
   * diagnostic since chunks for an unknown id usually indicate a protocol bug).
   */
  private handleChunkedFrame(chunk: BifrostMessage): void {
    // Streaming consumers receive each verified chunk directly without going
    // through the reassembly buffer. The queue removes itself from `streaming`
    // via its cleanup callback when it completes or errors.
    const streamingQueue = this.streaming.get(chunk.requestId)?.queue;
    if (streamingQueue) {
      // No ack here: the queue acks a chunk when it hands it to the consumer
      // (ackStreamChunk), so the server's ack window is the stream's
      // backpressure.
      const accepted = ingestStreamingChunk(streamingQueue, chunk);
      if (accepted) {
        // Re-arm the idle timer only while the stream is still registered. If
        // the final chunk just auto-completed the queue (or an overflow errored
        // it), its cleanup already ran removeStreamingQueue, and a timer armed
        // now would never be cleared.
        if (this.streaming.has(chunk.requestId)) {
          this.armStreamingTimeout(chunk.requestId);
        }
      }
      return;
    }

    const state = this.unary.get(chunk.requestId);
    if (!state) {
      if (this.abortedRequests.has(chunk.requestId)) {
        // Late chunk for a client-side-aborted request: expected, drop it
        // silently — but still ack it, otherwise the server's send window
        // stalls mid-response and blocks the shared socket for other traffic.
        // Once the final chunk of the response has passed through, the
        // tombstone has served its purpose and can be removed.
        this.sendChunkAck(chunk.requestId, chunk.chunkInfo.sequence);
        // Only a chunk that declares a real total (> 0) can be the final one.
        // A malformed/streaming frame reporting total === 0 would otherwise
        // satisfy `sequence + 1 >= total` on the very first late chunk, delete
        // the tombstone prematurely, and send every subsequent chunk down the
        // unknown-requestId path — re-spraying onError and dropping the ack,
        // which re-introduces the server send-window stall this tombstone fixes.
        if (
          chunk.chunkInfo.total > 0 &&
          chunk.chunkInfo.sequence + 1 >= chunk.chunkInfo.total
        ) {
          this.abortedRequests.delete(chunk.requestId);
        }
        return;
      }
      this.onError?.(
        new Error(
          `Received chunk for unknown requestId ${chunk.requestId} (sequence ${chunk.chunkInfo.sequence}/${chunk.chunkInfo.total})`
        )
      );
      return;
    }

    let reassembler = state.reassembler;
    if (!reassembler) {
      // Validate the declared size BEFORE allocating: a corrupt or malicious
      // header must reject this request, not allocate an oversized buffer or
      // throw a RangeError that would escape through onmessage and kill the
      // dispatch loop while the pending promise hangs until its timeout.
      if (chunk.chunkInfo.totalBytes > this.maxChunkedResponseBytes) {
        this.rejectPendingRequest(
          chunk.requestId,
          new Error(
            `Chunked response for request ${chunk.requestId} declares ` +
              `totalBytes ${chunk.chunkInfo.totalBytes}, exceeding the ` +
              `configured maximum of ${this.maxChunkedResponseBytes} bytes`
          )
        );
        return;
      }
      try {
        reassembler = new ChunkReassembler(
          chunk.requestId,
          chunk.chunkInfo.total,
          chunk.chunkInfo.totalBytes,
          this.onChunkProgress
            ? (received, total) =>
                this.onChunkProgress!(chunk.requestId, received, total)
            : undefined
        );
      } catch (err) {
        this.rejectPendingRequest(
          chunk.requestId,
          new Error(
            `Invalid chunked response header for request ${chunk.requestId}: ${
              err instanceof Error ? err.message : String(err)
            }`
          )
        );
        return;
      }
      state.reassembler = reassembler;
    }

    let assembled: Uint8Array | null;
    try {
      assembled = reassembler.addChunk(
        chunk.chunkInfo.sequence,
        chunk.chunkInfo.offset,
        chunk.payload,
        chunk.chunkInfo.checksum
      );
    } catch (err) {
      // Checksum or layout failure: reject the pending request and drop state.
      this.rejectPendingRequest(
        chunk.requestId,
        err instanceof Error ? err : new Error(String(err))
      );
      return;
    }

    // The chunk was verified and stored (or was a benign duplicate): ack it so
    // the server's backpressure window advances.
    this.sendChunkAck(chunk.requestId, chunk.chunkInfo.sequence);

    if (assembled === null) {
      return;
    }

    state.reassembler = undefined;

    let inner: BifrostMessage;
    try {
      inner = decodeMessage(assembled);
    } catch (err) {
      this.rejectPendingRequest(
        chunk.requestId,
        new Error(
          `Failed to decode reassembled chunked message for request ${chunk.requestId}: ${
            err instanceof Error ? err.message : String(err)
          }`
        )
      );
      return;
    }

    this.deliverComplete(inner);
  }

  /**
   * Delivers a fully-decoded (single-frame or reassembled) BifrostMessage to
   * its pending request promise. Frames with no matching pending entry are
   * silently dropped to match the existing single-frame behavior.
   */
  private deliverComplete(response: BifrostMessage): void {
    const pending = this.unary.get(response.requestId)?.pending;
    if (!pending) {
      // A non-chunked (or fully reassembled) frame is the end of the server's
      // response for this id — any abort tombstone is no longer needed.
      this.abortedRequests.delete(response.requestId);
      return;
    }

    clearTimeout(pending.timer);
    this.unary.delete(response.requestId);

    let data: unknown = null;
    let envelopeErrors: string[] = [];
    if (response.payload.length > 0) {
      try {
        // The payload is the standard GraphQL response envelope ({"data": ...,
        // "errors"?: [...]}) — the server renders it with the same registered
        // GraphQL serializer its HTTP endpoint uses, and its conformance tests
        // pin that wire shape. Unwrap exactly ONE envelope level,
        // unconditionally: shape-sniffing would misread a root field aliased
        // "data", and a payload that isn't an envelope is a server bug, not a
        // shape this client should silently accommodate. Resolving the RAW
        // envelope instead (the old behavior) double-wrapped every result and
        // white-screened the desktop editor's binary mode: callers read
        // result.data.<field> and found the envelope's "data" key instead.
        const envelope = JSON.parse(
          new TextDecoder().decode(response.payload)
        ) as { data?: unknown; errors?: Array<{ message?: string } | string> };
        data = envelope.data ?? null;
        envelopeErrors = (envelope.errors ?? []).map((e) =>
          typeof e === "string" ? e : e.message ?? "Unknown GraphQL error"
        );
      } catch (err) {
        // A malformed payload must reject the promise. The timer was already
        // cleared and the entry removed above, so without this the caller's
        // promise would hang forever with no timeout to rescue it.
        pending.reject(
          new Error(
            `Failed to parse response payload for request ${response.requestId}: ${
              err instanceof Error ? err.message : String(err)
            }`
          )
        );
        return;
      }
    }

    // Frame errors are authoritative (the server always mirrors execution
    // errors onto the frame); envelope errors only fill in if a server ever
    // reports a partial failure solely inside the envelope.
    const errors = response.errors.length > 0 ? response.errors : envelopeErrors;
    pending.resolve({ data, errors });
  }

  /**
   * Delivers a non-chunked Result/Error frame to a streaming consumer. Result
   * frames yield exactly one StreamChunk (sequence 0 of 1) and complete the
   * iterator; Error frames terminate the iterator with the joined error text.
   */
  private deliverStreamingNonChunked(
    response: BifrostMessage,
    queue: StreamingQueue
  ): void {
    if (response.type === BifrostMessageType.Error || response.errors.length > 0) {
      queue.error(
        new Error(
          response.errors.length > 0
            ? response.errors.join("; ")
            : `Server error for request ${response.requestId}`
        )
      );
      return;
    }

    queue.push({
      requestId: response.requestId,
      sequence: 0,
      totalChunks: 1,
      bytes: response.payload,
      isLast: true,
    });
    queue.complete();
  }

  private errorAllStreamingQueues(error: Error): void {
    // Snapshot values first because queue.error() runs the cleanup callback
    // which mutates `streaming` via removeStreamingQueue. Clear each idle timer
    // as we go: the entries are gone from the map before cleanup runs, so
    // removeStreamingQueue's clearStreamingTimeout would no longer find them.
    const states = Array.from(this.streaming.values());
    this.streaming.clear();
    for (const state of states) {
      if (state.timer !== undefined) {
        clearTimeout(state.timer);
      }
      state.queue.error(error);
    }
  }

  private rejectAllPending(error: Error): void {
    for (const [, state] of this.unary) {
      clearTimeout(state.pending.timer);
      state.pending.reject(error);
    }
    this.unary.clear();
    // Every request is gone, on the wire or queued. Cleared before the
    // streams are errored so their teardown leaves no tombstones behind.
    this.wireSlots.clear();
    this.sendQueue.length = 0;
    this.interruptedOnWire.clear();
    this.cancelRefusalRetry();
    this.abortedRequests.clear();
    this.errorAllStreamingQueues(error);
  }
}
