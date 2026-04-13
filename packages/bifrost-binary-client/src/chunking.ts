import { BinaryReader, BinaryWriter, WireType } from "@bufbuild/protobuf/wire";

/**
 * Chunked transport reassembly for BifrostQL binary protocol.
 *
 * ## Wire format (mirrors src/BifrostQL.Server/BifrostMessage.cs)
 *
 * The server's BifrostMessage is a flat protobuf-compatible envelope. Chunk-related
 * fields are top-level on the same envelope (NOT a nested ChunkInfo message):
 *
 *   field  1: request_id      (uint32, varint)
 *   field  2: type            (int32 enum, varint) - 4 = Chunk for chunked frames
 *   field  3: query           (string)
 *   field  4: variables_json  (string)
 *   field  5: payload         (bytes) - on Chunk frames, holds the fragment
 *   field  6: errors          (repeated string)
 *   field  7: chunk_sequence  (uint32, varint) - 0-based chunk index
 *   field  8: chunk_total     (uint32, varint) - total number of chunks
 *   field  9: chunk_offset    (uint64, varint) - byte offset of fragment in serialized message
 *   field 10: total_bytes     (uint64, varint) - total serialized message length before chunking
 *   field 11: chunk_checksum  (uint32, varint) - CRC32 of this chunk's payload bytes
 *   field 12: last_sequence   (uint32, varint) - resume support (Resume messages only)
 *
 * ## Reassembly contract (mirrors src/BifrostQL.Server/ChunkSender.cs +
 *    ChunkReceiver.cs)
 *
 * The server's ChunkSender first serializes the original Result message via
 * `BifrostMessage.ToBytes()`, then splits THOSE BYTES into fragments of size
 * `chunk_threshold` (default 64 KB). Each fragment is wrapped in a new
 * BifrostMessage with type=Chunk(4) and the metadata above.
 *
 * To reassemble: concatenate every chunk's payload at its declared `chunk_offset`
 * into a buffer of length `total_bytes`, then deserialize that buffer as a
 * BifrostMessage. The result is the original Result message with its full payload.
 *
 * ## Checksum algorithm
 *
 * Server uses `System.IO.Hashing.Crc32.HashToUInt32(chunkData)`, which is the
 * standard CRC-32/ISO-HDLC variant (polynomial 0xEDB88320, init 0xFFFFFFFF,
 * reflected input/output, final XOR 0xFFFFFFFF). This is the same CRC used by
 * zlib, gzip, PNG, and Ethernet. The client reimplements it inline (no deps)
 * using a 256-entry table.
 *
 * Reference vectors (verified against System.IO.Hashing on .NET 8):
 *   crc32("123456789") = 0xCBF43926  (CRC-32/ISO-HDLC canonical check)
 *   crc32([1,2,3])     = 0x55BC801D
 *   crc32("hello")     = 0x3610A686
 *   crc32([])          = 0x00000000
 */

/**
 * Protobuf field numbers for chunk-related fields on BifrostMessage. Keep these
 * aligned with src/BifrostQL.Server/BifrostMessage.cs.
 */
export const CHUNK_FIELD = {
  ChunkSequence: 7,
  ChunkTotal: 8,
  ChunkOffset: 9,
  TotalBytes: 10,
  ChunkChecksum: 11,
  LastSequence: 12,
} as const;

/**
 * Chunked-envelope metadata extracted from a BifrostMessage. Mirrors the
 * server's chunk fields. Absence on a non-chunked message is represented by
 * total === 0 (the default proto3 value, which the server omits from the wire).
 */
export interface ChunkInfo {
  /** 0-based chunk index. */
  sequence: number;
  /** Total number of chunks in the transfer. 0 means "not a chunked frame". */
  total: number;
  /** Byte offset of this fragment in the serialized full message. */
  offset: number;
  /** Length in bytes of the full serialized message before chunking. */
  totalBytes: number;
  /** CRC-32/ISO-HDLC checksum of this chunk's payload bytes. */
  checksum: number;
}

/**
 * Returns true when the chunk metadata describes a chunked frame (i.e. the
 * server emitted a non-zero chunk_total).
 */
export function isChunkedFrame(info: ChunkInfo): boolean {
  return info.total > 0;
}

/**
 * Creates an empty ChunkInfo with all proto3 default values.
 */
export function emptyChunkInfo(): ChunkInfo {
  return { sequence: 0, total: 0, offset: 0, totalBytes: 0, checksum: 0 };
}

/**
 * Decodes the chunk-related fields from a BifrostMessage's raw protobuf bytes.
 * Walks the same wire format as decodeMessage() but only extracts fields 7-11.
 * Used by tests that need to round-trip ChunkInfo independent of the full
 * BifrostMessage decoder. The client's decodeMessage() in index.ts decodes
 * these fields directly into the BifrostMessage shape.
 */
export function decodeChunkInfo(bytes: Uint8Array): ChunkInfo {
  const reader = new BinaryReader(bytes);
  const info = emptyChunkInfo();

  while (reader.pos < reader.len) {
    const [fieldNo, wireType] = reader.tag();
    switch (fieldNo) {
      case CHUNK_FIELD.ChunkSequence:
        info.sequence = reader.uint32();
        break;
      case CHUNK_FIELD.ChunkTotal:
        info.total = reader.uint32();
        break;
      case CHUNK_FIELD.ChunkOffset:
        info.offset = uint64ToNumber(reader.uint64());
        break;
      case CHUNK_FIELD.TotalBytes:
        info.totalBytes = uint64ToNumber(reader.uint64());
        break;
      case CHUNK_FIELD.ChunkChecksum:
        info.checksum = reader.uint32();
        break;
      default:
        reader.skip(wireType);
        break;
    }
  }

  return info;
}

/**
 * Encodes only the chunk fields into a fresh buffer. Test helper for
 * decodeChunkInfo round-trip checks; the production wire path encodes these
 * fields inline with the full BifrostMessage.
 */
export function encodeChunkInfo(info: ChunkInfo): Uint8Array {
  const writer = new BinaryWriter();
  if (info.sequence !== 0) {
    writer.tag(CHUNK_FIELD.ChunkSequence, WireType.Varint).uint32(info.sequence);
  }
  if (info.total !== 0) {
    writer.tag(CHUNK_FIELD.ChunkTotal, WireType.Varint).uint32(info.total);
  }
  if (info.offset !== 0) {
    writer.tag(CHUNK_FIELD.ChunkOffset, WireType.Varint).uint64(info.offset);
  }
  if (info.totalBytes !== 0) {
    writer.tag(CHUNK_FIELD.TotalBytes, WireType.Varint).uint64(info.totalBytes);
  }
  if (info.checksum !== 0) {
    writer.tag(CHUNK_FIELD.ChunkChecksum, WireType.Varint).uint32(info.checksum);
  }
  return writer.finish();
}

/**
 * uint64 fields from BinaryReader return either bigint or string depending on
 * value range. Chunk offsets and total payload sizes will always fit in
 * Number.MAX_SAFE_INTEGER for any sane payload (2^53 bytes ~= 9 PB), so we
 * narrow to number here. Values exceeding safe integer range throw.
 */
function uint64ToNumber(value: bigint | string): number {
  const big = typeof value === "string" ? BigInt(value) : value;
  if (big > BigInt(Number.MAX_SAFE_INTEGER)) {
    throw new Error(
      `Chunk offset/totalBytes ${big} exceeds Number.MAX_SAFE_INTEGER; ` +
        `payloads larger than 9 PB are not supported`
    );
  }
  return Number(big);
}

/**
 * CRC-32/ISO-HDLC table (polynomial 0xEDB88320, reflected). Built lazily on
 * first use. Matches System.IO.Hashing.Crc32 on the server.
 */
let crc32Table: Uint32Array | null = null;

function buildCrc32Table(): Uint32Array {
  const table = new Uint32Array(256);
  for (let i = 0; i < 256; i++) {
    let c = i;
    for (let k = 0; k < 8; k++) {
      c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    }
    table[i] = c >>> 0;
  }
  return table;
}

/**
 * Computes the CRC-32/ISO-HDLC checksum of the given bytes. Matches
 * System.IO.Hashing.Crc32 (init 0xFFFFFFFF, reflected, final XOR 0xFFFFFFFF).
 */
export function crc32(bytes: Uint8Array): number {
  if (!crc32Table) {
    crc32Table = buildCrc32Table();
  }
  const table = crc32Table;
  let crc = 0xffffffff;
  for (let i = 0; i < bytes.length; i++) {
    crc = table[(crc ^ bytes[i]!) & 0xff]! ^ (crc >>> 8);
  }
  return (crc ^ 0xffffffff) >>> 0;
}

/**
 * Verifies a chunk's CRC32 matches the expected value. Returns true on match.
 */
export function verifyChecksum(bytes: Uint8Array, expected: number): boolean {
  return crc32(bytes) === (expected >>> 0);
}

/**
 * Reassembles chunked BifrostMessage payloads into a single contiguous buffer.
 *
 * Mirrors the server's ChunkReceiver.ReassemblyState contract: each call to
 * `addChunk` validates the chunk's CRC32 against `chunk_checksum`, copies the
 * fragment into the buffer at its declared offset, and tracks completion.
 * Once `total` chunks have been received, `addChunk` returns the assembled
 * buffer (which the client then deserializes via decodeMessage). Out-of-order
 * arrival is supported because each chunk carries an explicit byte offset.
 *
 * Duplicate chunks (same sequence) are silently dropped (matches the server
 * behavior in ChunkReceiver.ReassemblyState.AddChunk).
 *
 * Checksum failure throws an Error containing the expected/actual values; the
 * caller is expected to reject the pending request and discard the reassembler.
 */
export class ChunkReassembler {
  private readonly buffer: Uint8Array;
  private readonly received: boolean[];
  private receivedCount = 0;
  private _lastReceivedSequence = -1;

  /**
   * @param requestId - The request_id this reassembler is for (for error context).
   * @param total - Total number of chunks expected.
   * @param totalBytes - Total length of the assembled buffer in bytes.
   * @param onProgress - Optional callback invoked once per accepted chunk with
   *   the cumulative received count. Called BEFORE the assembled buffer is
   *   returned for the final chunk.
   */
  constructor(
    public readonly requestId: number,
    public readonly total: number,
    public readonly totalBytes: number,
    private readonly onProgress?: (received: number, total: number) => void
  ) {
    if (total <= 0) {
      throw new Error(
        `ChunkReassembler requires total > 0, got ${total} for request ${requestId}`
      );
    }
    if (totalBytes < 0) {
      throw new Error(
        `ChunkReassembler requires totalBytes >= 0, got ${totalBytes} for request ${requestId}`
      );
    }
    this.buffer = new Uint8Array(totalBytes);
    this.received = new Array<boolean>(total).fill(false);
  }

  /**
   * The highest contiguous chunk sequence number received so far, or -1 if no
   * chunks have arrived. Used by the future Resume protocol (task pGkDg4FKSg7g)
   * to tell the server which chunk to retransmit from.
   */
  get lastReceivedSequence(): number {
    return this._lastReceivedSequence;
  }

  /**
   * Number of chunks received so far. Useful for diagnostics and tests.
   */
  get progress(): { received: number; total: number } {
    return { received: this.receivedCount, total: this.total };
  }

  /**
   * Adds a chunk fragment to the buffer after verifying its CRC32 checksum.
   * Returns the assembled buffer once all chunks have arrived, or null when
   * more chunks are still expected.
   *
   * Throws if the checksum mismatches, if the sequence is out of range, or if
   * the offset+length would overflow the declared totalBytes.
   */
  addChunk(
    sequence: number,
    offset: number,
    fragment: Uint8Array,
    expectedChecksum: number
  ): Uint8Array | null {
    if (sequence < 0 || sequence >= this.total) {
      throw new Error(
        `Chunk sequence ${sequence} out of range [0, ${this.total}) for request ${this.requestId}`
      );
    }
    if (!verifyChecksum(fragment, expectedChecksum)) {
      const actual = crc32(fragment);
      throw new Error(
        `CRC32 mismatch on chunk ${sequence} for request ${this.requestId}: ` +
          `expected ${expectedChecksum.toString(16).toUpperCase().padStart(8, "0")}, ` +
          `got ${actual.toString(16).toUpperCase().padStart(8, "0")}`
      );
    }
    if (offset < 0 || offset + fragment.length > this.totalBytes) {
      throw new Error(
        `Chunk ${sequence} offset ${offset}+${fragment.length} exceeds totalBytes ${this.totalBytes} for request ${this.requestId}`
      );
    }

    if (this.received[sequence]) {
      return null;
    }

    this.buffer.set(fragment, offset);
    this.received[sequence] = true;
    this.receivedCount++;
    if (sequence > this._lastReceivedSequence) {
      this._lastReceivedSequence = sequence;
    }

    this.onProgress?.(this.receivedCount, this.total);

    if (this.receivedCount === this.total) {
      return this.buffer;
    }
    return null;
  }
}
