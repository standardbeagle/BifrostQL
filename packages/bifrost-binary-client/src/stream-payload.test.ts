import { describe, expect, it } from "vitest";
import {
  BifrostMessageType,
  emptyChunkInfo,
  encodeMessage,
  type BifrostMessage,
} from "./index.js";
import { StreamPayloadExtractor } from "./stream-payload.js";

function resultFrame(payload: Uint8Array): Uint8Array {
  const msg: BifrostMessage = {
    requestId: 300, // two-byte varint
    type: BifrostMessageType.Result,
    query: "",
    variablesJson: "",
    payload,
    errors: [],
    chunkInfo: emptyChunkInfo(),
    lastSequence: 0,
  };
  return encodeMessage(msg);
}

function extractAll(serialized: Uint8Array, chunkSize: number): Uint8Array {
  const extractor = new StreamPayloadExtractor();
  const parts: Uint8Array[] = [];
  for (let offset = 0; offset < serialized.length; offset += chunkSize) {
    parts.push(extractor.take(serialized.slice(offset, offset + chunkSize)));
  }
  extractor.finish();
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let at = 0;
  for (const p of parts) {
    out.set(p, at);
    at += p.length;
  }
  return out;
}

describe("StreamPayloadExtractor", () => {
  it("returns exactly the payload for every fragment size", () => {
    const payload = new Uint8Array(200).map((_, i) => i); // two-byte length varint
    const serialized = resultFrame(payload);
    for (let size = 1; size <= serialized.length; size++) {
      expect(extractAll(serialized, size)).toEqual(payload);
    }
  });

  it("handles a payload whose length needs a three-byte varint", () => {
    const payload = new Uint8Array(70_000).map((_, i) => i % 251);
    expect(extractAll(resultFrame(payload), 65_536)).toEqual(payload);
  });

  it("refuses a message that stops inside the payload", () => {
    const serialized = resultFrame(new Uint8Array(50).fill(1));
    const extractor = new StreamPayloadExtractor();
    extractor.take(serialized.slice(0, serialized.length - 1));
    expect(() => extractor.finish()).toThrowError(/middle of a field/);
  });
});
