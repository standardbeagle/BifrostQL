import { BinaryReader, BinaryWriter, WireType } from "@bufbuild/protobuf/wire";
import {
  CHUNK_FIELD,
  ChunkReassembler,
  emptyChunkInfo,
  isChunkedFrame,
  type ChunkInfo,
} from "./chunking.js";

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

interface IWebSocketConstructor {
  new (url: string): IWebSocket;
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
}

interface PendingRequest {
  resolve: (result: BifrostQueryResult) => void;
  reject: (error: Error) => void;
  timer: ReturnType<typeof setTimeout>;
}

/**
 * Result of a BifrostQL query or mutation.
 */
export interface BifrostQueryResult {
  /** Parsed JSON response data, or null if the response contained no payload. */
  data: unknown;
  /** Error messages from the server, empty array on success. */
  errors: string[];
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
export class BifrostBinaryClient {
  private ws: IWebSocket | null = null;
  private nextRequestId = 1;
  private readonly pending = new Map<number, PendingRequest>();
  private readonly reassemblers = new Map<number, ChunkReassembler>();
  private readonly wsConstructor: IWebSocketConstructor;
  private readonly url: string;
  private readonly requestTimeoutMs: number;
  private readonly onOpen?: () => void;
  private readonly onClose?: (code: number, reason: string) => void;
  private readonly onError?: (error: Error) => void;
  private readonly onChunkProgress?: (
    requestId: number,
    received: number,
    total: number
  ) => void;

  constructor(options: BifrostClientOptions) {
    this.url = options.url;
    this.requestTimeoutMs = options.requestTimeoutMs ?? 30_000;
    this.onOpen = options.onOpen;
    this.onClose = options.onClose;
    this.onError = options.onError;
    this.onChunkProgress = options.onChunkProgress;

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
  }

  /**
   * Opens the WebSocket connection. Resolves when the connection is established,
   * rejects if the connection fails.
   */
  connect(): Promise<void> {
    return new Promise((resolve, reject) => {
      if (this.ws && this.ws.readyState === this.wsConstructor.OPEN) {
        resolve();
        return;
      }

      const ws = new this.wsConstructor(this.url);
      ws.binaryType = "arraybuffer";

      ws.onopen = () => {
        this.ws = ws;
        this.onOpen?.();
        resolve();
      };

      ws.onerror = () => {
        const err = new Error("WebSocket connection failed");
        this.onError?.(err);
        if (!this.ws) {
          reject(err);
        }
      };

      ws.onclose = (event) => {
        this.rejectAllPending(
          new Error(`Connection closed: ${event.code} ${event.reason}`)
        );
        this.ws = null;
        this.onClose?.(event.code, event.reason);
      };

      ws.onmessage = (event) => {
        this.handleMessage(event.data as ArrayBuffer);
      };
    });
  }

  /**
   * Sends a GraphQL query over the binary transport.
   * Returns a promise that resolves when the server responds with the matching request_id.
   */
  query(
    queryText: string,
    variables?: Record<string, unknown>
  ): Promise<BifrostQueryResult> {
    return this.send(BifrostMessageType.Query, queryText, variables);
  }

  /**
   * Sends a GraphQL mutation over the binary transport.
   * Returns a promise that resolves when the server responds with the matching request_id.
   */
  mutate(
    mutationText: string,
    variables?: Record<string, unknown>
  ): Promise<BifrostQueryResult> {
    return this.send(BifrostMessageType.Mutation, mutationText, variables);
  }

  /** Whether the WebSocket connection is currently open. */
  get connected(): boolean {
    return this.ws?.readyState === this.wsConstructor.OPEN;
  }

  /** Number of requests currently awaiting responses. */
  get pendingCount(): number {
    return this.pending.size;
  }

  /**
   * Closes the WebSocket connection gracefully. Drops any in-progress chunk
   * reassemblers so their buffers can be garbage collected.
   */
  close(): void {
    this.reassemblers.clear();
    if (this.ws) {
      this.ws.close(1000, "Client closed");
      this.ws = null;
    }
  }

  private send(
    type: BifrostMessageType,
    queryText: string,
    variables?: Record<string, unknown>
  ): Promise<BifrostQueryResult> {
    return new Promise((resolve, reject) => {
      if (!this.ws || this.ws.readyState !== this.wsConstructor.OPEN) {
        reject(new Error("Not connected"));
        return;
      }

      const requestId = this.nextRequestId++;
      if (this.nextRequestId > 0xffffffff) {
        this.nextRequestId = 1;
      }

      const timer = setTimeout(() => {
        this.pending.delete(requestId);
        reject(new Error(`Request ${requestId} timed out`));
      }, this.requestTimeoutMs);

      this.pending.set(requestId, { resolve, reject, timer });

      const msg: BifrostMessage = {
        requestId,
        type,
        query: queryText,
        variablesJson: variables ? JSON.stringify(variables) : "",
        payload: new Uint8Array(0),
        errors: [],
        chunkInfo: emptyChunkInfo(),
      };

      const bytes = encodeMessage(msg);
      this.ws.send(bytes);
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

    if (isChunkedFrame(response.chunkInfo)) {
      this.handleChunkedFrame(response);
      return;
    }

    this.deliverComplete(response);
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
    if (!this.pending.has(chunk.requestId)) {
      this.onError?.(
        new Error(
          `Received chunk for unknown requestId ${chunk.requestId} (sequence ${chunk.chunkInfo.sequence}/${chunk.chunkInfo.total})`
        )
      );
      return;
    }

    let reassembler = this.reassemblers.get(chunk.requestId);
    if (!reassembler) {
      reassembler = new ChunkReassembler(
        chunk.requestId,
        chunk.chunkInfo.total,
        chunk.chunkInfo.totalBytes,
        this.onChunkProgress
          ? (received, total) =>
              this.onChunkProgress!(chunk.requestId, received, total)
          : undefined
      );
      this.reassemblers.set(chunk.requestId, reassembler);
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
      this.reassemblers.delete(chunk.requestId);
      const pending = this.pending.get(chunk.requestId);
      if (pending) {
        clearTimeout(pending.timer);
        this.pending.delete(chunk.requestId);
        pending.reject(err instanceof Error ? err : new Error(String(err)));
      }
      return;
    }

    if (assembled === null) {
      return;
    }

    this.reassemblers.delete(chunk.requestId);

    let inner: BifrostMessage;
    try {
      inner = decodeMessage(assembled);
    } catch (err) {
      const pending = this.pending.get(chunk.requestId);
      if (pending) {
        clearTimeout(pending.timer);
        this.pending.delete(chunk.requestId);
        pending.reject(
          new Error(
            `Failed to decode reassembled chunked message for request ${chunk.requestId}: ${
              err instanceof Error ? err.message : String(err)
            }`
          )
        );
      }
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
    const pending = this.pending.get(response.requestId);
    if (!pending) {
      return;
    }

    clearTimeout(pending.timer);
    this.pending.delete(response.requestId);

    let data: unknown = null;
    if (response.payload.length > 0) {
      const decoded = new TextDecoder().decode(response.payload);
      data = JSON.parse(decoded);
    }

    pending.resolve({ data, errors: response.errors });
  }

  private rejectAllPending(error: Error): void {
    for (const [, pending] of this.pending) {
      clearTimeout(pending.timer);
      pending.reject(error);
    }
    this.pending.clear();
    this.reassemblers.clear();
  }
}
