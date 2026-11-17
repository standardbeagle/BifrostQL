/**
 * Pulls the payload bytes out of a chunked response, one chunk at a time.
 *
 * The server chunks a large Result by slicing its whole serialized
 * BifrostMessage (`ChunkSender.SplitIntoChunks`): the envelope fields
 * (request_id, type, errors, then the payload's tag and length), then the
 * payload bytes. Chunk 0 therefore starts with protobuf framing. The streaming
 * API promises payload bytes, the same bytes a non-chunked Result yields, so
 * each chunk's fragment goes through `take`, which returns only the part of it
 * that belongs to the payload field.
 *
 * Fragments must be fed in sequence order. Fields other than the payload are
 * parsed incrementally, so a fragment boundary may fall anywhere, including
 * inside a tag or length varint.
 */
import { BifrostMessageType } from "./index.js";

const EMPTY = new Uint8Array(0);
const WIRE_VARINT = 0;
const WIRE_LENGTH_DELIMITED = 2;
const FIELD_TYPE = 2;
const FIELD_PAYLOAD = 5;
const FIELD_ERRORS = 6;

type ParsedField =
  | { kind: "varint"; field: number; value: number; end: number }
  | { kind: "bytes"; field: number; value: Uint8Array; end: number }
  | { kind: "payload-start"; length: number; end: number };

export class StreamPayloadExtractor {
  /** Envelope bytes that did not yet form a whole field. */
  private carry: Uint8Array = EMPTY;
  /** Payload bytes still expected; > 0 while inside the payload field. */
  private payloadRemaining = 0;
  private type: number = BifrostMessageType.Query;
  private readonly errors: string[] = [];

  /**
   * Returns the payload bytes contained in `fragment` (possibly empty).
   * Throws when the envelope reports server errors, the same outcome as a
   * non-chunked Result that carries errors, or when it is not valid protobuf.
   */
  take(fragment: Uint8Array): Uint8Array {
    const input = this.carry.length > 0 ? concat([this.carry, fragment]) : fragment;
    this.carry = EMPTY;
    const out: Uint8Array[] = [];
    let pos = 0;
    while (pos < input.length) {
      if (this.payloadRemaining > 0) {
        const n = Math.min(this.payloadRemaining, input.length - pos);
        out.push(input.subarray(pos, pos + n));
        pos += n;
        this.payloadRemaining -= n;
        continue;
      }
      const parsed = readField(input, pos);
      if (parsed === null) {
        this.carry = input.slice(pos);
        break;
      }
      pos = parsed.end;
      this.accept(parsed);
    }
    return out.length === 1 ? out[0]! : concat(out);
  }

  /**
   * Called after the last fragment. Throws if the message stopped mid-field,
   * or if it carried errors after the payload (the server writes them first,
   * so this only catches a non-conforming sender).
   */
  finish(): void {
    if (this.payloadRemaining > 0 || this.carry.length > 0) {
      throw new Error("Chunked response ended in the middle of a field");
    }
    this.throwIfServerError();
  }

  private accept(parsed: ParsedField): void {
    if (parsed.kind === "payload-start") {
      // Errors precede the payload on the wire: fail before yielding any
      // payload byte, as the non-chunked path does.
      this.throwIfServerError();
      this.payloadRemaining = parsed.length;
      return;
    }
    if (parsed.kind === "varint" && parsed.field === FIELD_TYPE) {
      this.type = parsed.value;
    } else if (parsed.kind === "bytes" && parsed.field === FIELD_ERRORS) {
      this.errors.push(new TextDecoder().decode(parsed.value));
    }
  }

  private throwIfServerError(): void {
    if (this.errors.length > 0) {
      throw new Error(this.errors.join("; "));
    }
    if (this.type === BifrostMessageType.Error) {
      throw new Error("Server returned an Error message without error text");
    }
  }
}

/** Reads one field at `pos`; null when `input` ends before the field does. */
function readField(input: Uint8Array, pos: number): ParsedField | null {
  const tag = readVarint(input, pos);
  if (tag === null) return null;
  const field = Math.floor(tag.value / 8);
  const wireType = tag.value % 8;
  if (wireType === WIRE_VARINT) {
    const value = readVarint(input, tag.end);
    if (value === null) return null;
    return { kind: "varint", field, value: value.value, end: value.end };
  }
  if (wireType !== WIRE_LENGTH_DELIMITED) {
    throw new Error(`Chunked response has unsupported protobuf wire type ${wireType}`);
  }
  const length = readVarint(input, tag.end);
  if (length === null) return null;
  if (field === FIELD_PAYLOAD) {
    return { kind: "payload-start", length: length.value, end: length.end };
  }
  const end = length.end + length.value;
  if (end > input.length) return null;
  return { kind: "bytes", field, value: input.subarray(length.end, end), end };
}

function readVarint(
  input: Uint8Array,
  pos: number
): { value: number; end: number } | null {
  let value = 0;
  let scale = 1;
  for (let i = pos; i < input.length; i++) {
    if (i - pos >= 10) {
      throw new Error("Chunked response has a malformed protobuf varint");
    }
    const byte = input[i]!;
    value += (byte & 0x7f) * scale;
    scale *= 128;
    if ((byte & 0x80) === 0) {
      return { value, end: i + 1 };
    }
  }
  return null;
}

function concat(parts: Uint8Array[]): Uint8Array {
  let length = 0;
  for (const p of parts) length += p.length;
  const out = new Uint8Array(length);
  let offset = 0;
  for (const p of parts) {
    out.set(p, offset);
    offset += p.length;
  }
  return out;
}
