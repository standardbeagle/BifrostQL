import { describe, expect, it, vi } from "vitest";
import { BinaryWriter, WireType } from "@bufbuild/protobuf/wire";
import {
  ChunkReassembler,
  crc32,
  decodeChunkInfo,
  emptyChunkInfo,
  encodeChunkInfo,
  isChunkedFrame,
  verifyChecksum,
  type ChunkInfo,
} from "./chunking.js";

/**
 * Reference vectors verified against System.IO.Hashing.Crc32 on .NET 8.
 * These pin the client implementation to the same CRC-32/ISO-HDLC variant the
 * server uses in src/BifrostQL.Server/ChunkSender.cs.
 */
describe("crc32", () => {
  it("matches the canonical CRC-32/ISO-HDLC check vector for '123456789'", () => {
    const data = new TextEncoder().encode("123456789");
    expect(crc32(data)).toBe(0xcbf43926);
  });

  it("matches reference vector for [1,2,3]", () => {
    expect(crc32(new Uint8Array([1, 2, 3]))).toBe(0x55bc801d);
  });

  it("matches reference vector for 'hello'", () => {
    expect(crc32(new TextEncoder().encode("hello"))).toBe(0x3610a686);
  });

  it("matches reference vector for [0..255]", () => {
    const data = new Uint8Array(256);
    for (let i = 0; i < 256; i++) data[i] = i;
    expect(crc32(data)).toBe(0x29058c73);
  });

  it("returns 0 for an empty buffer", () => {
    expect(crc32(new Uint8Array(0))).toBe(0);
  });

  it("is deterministic", () => {
    const data = new Uint8Array([9, 8, 7, 6, 5, 4, 3, 2, 1]);
    expect(crc32(data)).toBe(crc32(data));
  });
});

describe("verifyChecksum", () => {
  it("returns true on a matching checksum", () => {
    const data = new TextEncoder().encode("hello");
    expect(verifyChecksum(data, 0x3610a686)).toBe(true);
  });

  it("returns false on a mismatching checksum", () => {
    const data = new TextEncoder().encode("hello");
    expect(verifyChecksum(data, 0xdeadbeef)).toBe(false);
  });
});

describe("decodeChunkInfo / encodeChunkInfo", () => {
  it("round-trips a fully populated ChunkInfo", () => {
    const original: ChunkInfo = {
      sequence: 5,
      total: 12,
      offset: 65536,
      totalBytes: 786432,
      checksum: 0xabcd1234,
    };
    expect(decodeChunkInfo(encodeChunkInfo(original))).toEqual(original);
  });

  it("round-trips defaults as zeroes", () => {
    expect(decodeChunkInfo(encodeChunkInfo(emptyChunkInfo()))).toEqual(
      emptyChunkInfo()
    );
  });

  it("survives mixed defaults (only some fields set)", () => {
    const partial: ChunkInfo = {
      sequence: 0,
      total: 3,
      offset: 0,
      totalBytes: 30,
      checksum: 0xdeadbeef,
    };
    expect(decodeChunkInfo(encodeChunkInfo(partial))).toEqual(partial);
  });

  it("handles large uint64 offsets within safe integer range", () => {
    const big: ChunkInfo = {
      sequence: 1,
      total: 2,
      offset: 4 * 1024 * 1024 * 1024, // 4 GB
      totalBytes: 8 * 1024 * 1024 * 1024, // 8 GB
      checksum: 1,
    };
    expect(decodeChunkInfo(encodeChunkInfo(big))).toEqual(big);
  });

  it("skips unknown fields embedded among chunk fields", () => {
    // Build a buffer that interleaves a chunk_total field with an unknown
    // varint field (field 99). decodeChunkInfo must skip the unknown field and
    // still extract chunk_total.
    const tagUnknown = 99 << 3; // wire type 0 (varint)
    const tagBytes: number[] = [];
    let v = tagUnknown;
    while (v >= 0x80) {
      tagBytes.push((v & 0x7f) | 0x80);
      v >>>= 7;
    }
    tagBytes.push(v);
    const unknownField = new Uint8Array([...tagBytes, 0]);

    const known = encodeChunkInfo({
      sequence: 0,
      total: 4,
      offset: 0,
      totalBytes: 0,
      checksum: 0,
    });

    const combined = new Uint8Array(unknownField.length + known.length);
    combined.set(unknownField, 0);
    combined.set(known, unknownField.length);

    const decoded = decodeChunkInfo(combined);
    expect(decoded.total).toBe(4);
  });

  it("throws when a uint64 chunk field exceeds Number.MAX_SAFE_INTEGER", () => {
    // Encode a chunk_offset of 2^53 + 2, which exceeds MAX_SAFE_INTEGER.
    // Use a hand-built BinaryWriter so we can pass a bigint directly.
    const writer = new BinaryWriter();
    writer
      .tag(9, WireType.Varint)
      .uint64(BigInt(Number.MAX_SAFE_INTEGER) + 2n);
    const bytes = writer.finish();
    expect(() => decodeChunkInfo(bytes)).toThrowError(
      /exceeds Number\.MAX_SAFE_INTEGER/
    );
  });
});

describe("isChunkedFrame", () => {
  it("returns false on an empty ChunkInfo", () => {
    expect(isChunkedFrame(emptyChunkInfo())).toBe(false);
  });

  it("returns true when total > 0", () => {
    expect(
      isChunkedFrame({
        sequence: 0,
        total: 4,
        offset: 0,
        totalBytes: 100,
        checksum: 0,
      })
    ).toBe(true);
  });
});

describe("ChunkReassembler", () => {
  /**
   * Helper: split a payload into N equal-sized fragments and pre-compute their
   * CRC32 checksums, mirroring how ChunkSender produces chunk metadata.
   */
  function splitPayload(payload: Uint8Array, chunkSize: number) {
    const total = Math.ceil(payload.length / chunkSize);
    return Array.from({ length: total }, (_, i) => {
      const offset = i * chunkSize;
      const length = Math.min(chunkSize, payload.length - offset);
      const fragment = payload.slice(offset, offset + length);
      return {
        sequence: i,
        offset,
        fragment,
        checksum: crc32(fragment),
      };
    });
  }

  it("rejects total <= 0", () => {
    expect(() => new ChunkReassembler(1, 0, 0)).toThrowError(/total > 0/);
    expect(() => new ChunkReassembler(1, -1, 10)).toThrowError(/total > 0/);
  });

  it("rejects negative totalBytes", () => {
    expect(() => new ChunkReassembler(1, 1, -1)).toThrowError(
      /totalBytes >= 0/
    );
  });

  it("reassembles 3 chunks delivered in order", () => {
    const payload = new Uint8Array(30);
    for (let i = 0; i < 30; i++) payload[i] = i + 1;
    const chunks = splitPayload(payload, 10);
    expect(chunks).toHaveLength(3);

    const r = new ChunkReassembler(7, chunks.length, payload.length);
    expect(r.addChunk(0, chunks[0]!.offset, chunks[0]!.fragment, chunks[0]!.checksum)).toBeNull();
    expect(r.addChunk(1, chunks[1]!.offset, chunks[1]!.fragment, chunks[1]!.checksum)).toBeNull();
    const result = r.addChunk(
      2,
      chunks[2]!.offset,
      chunks[2]!.fragment,
      chunks[2]!.checksum
    );

    expect(result).not.toBeNull();
    expect(Array.from(result!)).toEqual(Array.from(payload));
  });

  it("reassembles chunks delivered out of order", () => {
    const payload = new Uint8Array(25);
    for (let i = 0; i < 25; i++) payload[i] = (i * 7) & 0xff;
    const chunks = splitPayload(payload, 7);
    // 25 / 7 = 4 chunks (sizes 7,7,7,4)
    expect(chunks).toHaveLength(4);

    const r = new ChunkReassembler(11, chunks.length, payload.length);
    // Reverse order
    expect(
      r.addChunk(3, chunks[3]!.offset, chunks[3]!.fragment, chunks[3]!.checksum)
    ).toBeNull();
    expect(
      r.addChunk(1, chunks[1]!.offset, chunks[1]!.fragment, chunks[1]!.checksum)
    ).toBeNull();
    expect(
      r.addChunk(2, chunks[2]!.offset, chunks[2]!.fragment, chunks[2]!.checksum)
    ).toBeNull();
    const final = r.addChunk(
      0,
      chunks[0]!.offset,
      chunks[0]!.fragment,
      chunks[0]!.checksum
    );
    expect(final).not.toBeNull();
    expect(Array.from(final!)).toEqual(Array.from(payload));
  });

  it("treats a single-chunk payload as a complete delivery", () => {
    const payload = new TextEncoder().encode("solo");
    const r = new ChunkReassembler(2, 1, payload.length);
    const result = r.addChunk(0, 0, payload, crc32(payload));
    expect(result).not.toBeNull();
    expect(new TextDecoder().decode(result!)).toBe("solo");
  });

  it("fires onProgress once per accepted chunk with monotonically increasing received", () => {
    const payload = new Uint8Array(40);
    for (let i = 0; i < 40; i++) payload[i] = i;
    const chunks = splitPayload(payload, 8);
    expect(chunks).toHaveLength(5);

    const onProgress = vi.fn();
    const r = new ChunkReassembler(3, chunks.length, payload.length, onProgress);
    for (const c of chunks) {
      r.addChunk(c.sequence, c.offset, c.fragment, c.checksum);
    }

    expect(onProgress).toHaveBeenCalledTimes(5);
    const received = onProgress.mock.calls.map((c) => c[0] as number);
    expect(received).toEqual([1, 2, 3, 4, 5]);
    for (const call of onProgress.mock.calls) {
      expect(call[1]).toBe(5);
    }
  });

  it("ignores duplicate chunks without changing progress", () => {
    const payload = new Uint8Array([1, 2, 3, 4, 5, 6]);
    const chunks = splitPayload(payload, 3);
    const onProgress = vi.fn();
    const r = new ChunkReassembler(4, chunks.length, payload.length, onProgress);

    r.addChunk(0, chunks[0]!.offset, chunks[0]!.fragment, chunks[0]!.checksum);
    // Duplicate
    expect(
      r.addChunk(0, chunks[0]!.offset, chunks[0]!.fragment, chunks[0]!.checksum)
    ).toBeNull();
    expect(onProgress).toHaveBeenCalledTimes(1);
    expect(r.progress).toEqual({ received: 1, total: 2 });

    r.addChunk(1, chunks[1]!.offset, chunks[1]!.fragment, chunks[1]!.checksum);
    expect(onProgress).toHaveBeenCalledTimes(2);
  });

  it("throws on a checksum mismatch with both expected and actual values", () => {
    const payload = new TextEncoder().encode("hello world");
    const r = new ChunkReassembler(5, 1, payload.length);
    expect(() => r.addChunk(0, 0, payload, 0xdeadbeef)).toThrowError(
      /CRC32 mismatch on chunk 0 for request 5/
    );
  });

  it("throws when sequence is out of range", () => {
    const r = new ChunkReassembler(6, 2, 10);
    expect(() => r.addChunk(2, 0, new Uint8Array(5), crc32(new Uint8Array(5)))).toThrowError(
      /sequence 2 out of range/
    );
    expect(() => r.addChunk(-1, 0, new Uint8Array(5), crc32(new Uint8Array(5)))).toThrowError(
      /sequence -1 out of range/
    );
  });

  it("throws when offset+length would overflow declared totalBytes", () => {
    const r = new ChunkReassembler(7, 2, 10);
    const fragment = new Uint8Array([1, 2, 3, 4, 5]);
    expect(() => r.addChunk(0, 8, fragment, crc32(fragment))).toThrowError(
      /offset 8\+5 exceeds totalBytes 10/
    );
  });

  it("tracks lastReceivedSequence as the highest sequence accepted", () => {
    const payload = new Uint8Array(20);
    const chunks = splitPayload(payload, 5);
    const r = new ChunkReassembler(8, chunks.length, payload.length);
    expect(r.lastReceivedSequence).toBe(-1);

    r.addChunk(2, chunks[2]!.offset, chunks[2]!.fragment, chunks[2]!.checksum);
    expect(r.lastReceivedSequence).toBe(2);

    r.addChunk(0, chunks[0]!.offset, chunks[0]!.fragment, chunks[0]!.checksum);
    expect(r.lastReceivedSequence).toBe(2);

    r.addChunk(3, chunks[3]!.offset, chunks[3]!.fragment, chunks[3]!.checksum);
    expect(r.lastReceivedSequence).toBe(3);
  });
});
