/**
 * Minimal proto3 parser for the schema shape emitted by BifrostQL's
 * ProtoSchemaGenerator. This is NOT a general-purpose proto3 parser — it
 * understands exactly the constructs the server produces today:
 *
 *   - syntax = "proto3";
 *   - package <name>;
 *   - message <Name> { <fields> }
 *   - field declarations with optional `optional` / `repeated`, scalar types,
 *     and a positive field number.
 *   - enum <Name> { NAME = NUMBER; ... }
 *   - oneof <name> { <fields> } — flattened into the parent message's fields.
 *   - line comments (//) and block comments (/* ... *\/).
 *
 * Anything outside this subset throws a descriptive error including the line
 * and column where parsing failed. Keeping the surface small means we can
 * react to schema-generator changes immediately rather than silently dropping
 * fields a heavyweight parser would tolerate.
 */

export interface ProtoField {
  /** Field name as written in the .proto file (e.g. `user_id`). */
  name: string;
  /** Raw type token (e.g. `int32`, `string`, or a message/enum reference). */
  type: string;
  /** True when the field was declared `repeated`. */
  repeated: boolean;
  /** True when the field was declared `optional` (proto3 explicit presence). */
  optional: boolean;
  /** Field number. */
  number: number;
}

export interface ProtoMessage {
  name: string;
  fields: ProtoField[];
}

export interface ProtoEnumValue {
  name: string;
  number: number;
}

export interface ProtoEnum {
  name: string;
  values: ProtoEnumValue[];
}

export interface ProtoSchema {
  /** Optional `package` declaration value. */
  packageName: string | null;
  messages: ProtoMessage[];
  enums: ProtoEnum[];
}

/** Position information for parser errors. */
export interface ProtoParseError extends Error {
  line: number;
  column: number;
}

class Cursor {
  private pos = 0;

  constructor(private readonly src: string) {}

  get done(): boolean {
    return this.pos >= this.src.length;
  }

  peek(offset = 0): string {
    return this.src[this.pos + offset] ?? "";
  }

  advance(count = 1): void {
    this.pos += count;
  }

  startsWith(text: string): boolean {
    return this.src.startsWith(text, this.pos);
  }

  position(): { line: number; column: number } {
    let line = 1;
    let column = 1;
    for (let i = 0; i < this.pos; i++) {
      if (this.src[i] === "\n") {
        line++;
        column = 1;
      } else {
        column++;
      }
    }
    return { line, column };
  }
}

function fail(cursor: Cursor, message: string): never {
  const { line, column } = cursor.position();
  const err = new Error(`Proto parse error at line ${line}, column ${column}: ${message}`) as ProtoParseError;
  err.line = line;
  err.column = column;
  throw err;
}

function skipTrivia(cursor: Cursor): void {
  while (!cursor.done) {
    const ch = cursor.peek();
    if (ch === " " || ch === "\t" || ch === "\r" || ch === "\n") {
      cursor.advance();
      continue;
    }
    if (cursor.startsWith("//")) {
      while (!cursor.done && cursor.peek() !== "\n") cursor.advance();
      continue;
    }
    if (cursor.startsWith("/*")) {
      cursor.advance(2);
      while (!cursor.done && !cursor.startsWith("*/")) cursor.advance();
      if (cursor.done) fail(cursor, "unterminated block comment");
      cursor.advance(2);
      continue;
    }
    return;
  }
}

const IDENT_START = /[A-Za-z_]/;
const IDENT_REST = /[A-Za-z0-9_]/;

function readIdentifier(cursor: Cursor, what: string): string {
  skipTrivia(cursor);
  if (cursor.done || !IDENT_START.test(cursor.peek())) {
    fail(cursor, `expected ${what}`);
  }
  let result = "";
  while (!cursor.done && IDENT_REST.test(cursor.peek())) {
    result += cursor.peek();
    cursor.advance();
  }
  return result;
}

function readDottedIdentifier(cursor: Cursor, what: string): string {
  let id = readIdentifier(cursor, what);
  while (cursor.peek() === ".") {
    cursor.advance();
    id += "." + readIdentifier(cursor, what);
  }
  return id;
}

function expectChar(cursor: Cursor, ch: string): void {
  skipTrivia(cursor);
  if (cursor.peek() !== ch) {
    fail(cursor, `expected '${ch}'`);
  }
  cursor.advance();
}

function readQuotedString(cursor: Cursor): string {
  skipTrivia(cursor);
  const quote = cursor.peek();
  if (quote !== '"' && quote !== "'") fail(cursor, "expected quoted string");
  cursor.advance();
  let result = "";
  while (!cursor.done && cursor.peek() !== quote) {
    if (cursor.peek() === "\\") {
      cursor.advance();
      result += cursor.peek();
      cursor.advance();
      continue;
    }
    result += cursor.peek();
    cursor.advance();
  }
  if (cursor.done) fail(cursor, "unterminated string literal");
  cursor.advance();
  return result;
}

function readInteger(cursor: Cursor): number {
  skipTrivia(cursor);
  let text = "";
  if (cursor.peek() === "-") {
    text += cursor.peek();
    cursor.advance();
  }
  if (!/[0-9]/.test(cursor.peek())) fail(cursor, "expected integer literal");
  while (!cursor.done && /[0-9]/.test(cursor.peek())) {
    text += cursor.peek();
    cursor.advance();
  }
  const value = Number.parseInt(text, 10);
  if (Number.isNaN(value)) fail(cursor, `invalid integer literal '${text}'`);
  return value;
}

function consumeKeyword(cursor: Cursor, keyword: string): boolean {
  skipTrivia(cursor);
  if (!cursor.startsWith(keyword)) return false;
  // Make sure the keyword isn't actually a longer identifier prefix.
  const next = cursor.peek(keyword.length);
  if (next && IDENT_REST.test(next)) return false;
  cursor.advance(keyword.length);
  return true;
}

function parseField(cursor: Cursor): ProtoField {
  // Field qualifiers
  let repeated = false;
  let optional = false;
  if (consumeKeyword(cursor, "repeated")) {
    repeated = true;
  } else if (consumeKeyword(cursor, "optional")) {
    optional = true;
  }

  const type = readDottedIdentifier(cursor, "field type");
  const name = readIdentifier(cursor, "field name");
  expectChar(cursor, "=");
  const number = readInteger(cursor);
  // Optional [field options] block — we tolerate but ignore it.
  skipTrivia(cursor);
  if (cursor.peek() === "[") {
    let depth = 0;
    while (!cursor.done) {
      const ch = cursor.peek();
      if (ch === "[") depth++;
      else if (ch === "]") {
        depth--;
        cursor.advance();
        if (depth === 0) break;
        continue;
      }
      cursor.advance();
    }
  }
  expectChar(cursor, ";");

  return { name, type, repeated, optional, number };
}

function parseOneof(cursor: Cursor, parent: ProtoField[]): void {
  // Already consumed `oneof`. Read name + body.
  readIdentifier(cursor, "oneof name");
  expectChar(cursor, "{");
  while (true) {
    skipTrivia(cursor);
    if (cursor.peek() === "}") {
      cursor.advance();
      return;
    }
    if (cursor.done) fail(cursor, "unterminated oneof body");
    parent.push(parseField(cursor));
  }
}

function parseMessage(cursor: Cursor): ProtoMessage {
  const name = readIdentifier(cursor, "message name");
  expectChar(cursor, "{");

  const fields: ProtoField[] = [];
  while (true) {
    skipTrivia(cursor);
    if (cursor.peek() === "}") {
      cursor.advance();
      return { name, fields };
    }
    if (cursor.done) fail(cursor, "unterminated message body");

    if (consumeKeyword(cursor, "oneof")) {
      parseOneof(cursor, fields);
      continue;
    }
    if (consumeKeyword(cursor, "reserved")) {
      // Skip until ';'.
      while (!cursor.done && cursor.peek() !== ";") cursor.advance();
      expectChar(cursor, ";");
      continue;
    }
    fields.push(parseField(cursor));
  }
}

function parseEnum(cursor: Cursor): ProtoEnum {
  const name = readIdentifier(cursor, "enum name");
  expectChar(cursor, "{");
  const values: ProtoEnumValue[] = [];
  while (true) {
    skipTrivia(cursor);
    if (cursor.peek() === "}") {
      cursor.advance();
      return { name, values };
    }
    if (cursor.done) fail(cursor, "unterminated enum body");
    const valueName = readIdentifier(cursor, "enum value name");
    expectChar(cursor, "=");
    const number = readInteger(cursor);
    expectChar(cursor, ";");
    values.push({ name: valueName, number });
  }
}

/**
 * Parses a .proto3 source file into a ProtoSchema.
 * Throws ProtoParseError with line/column info on any unexpected token.
 */
export function parseProto(text: string): ProtoSchema {
  const cursor = new Cursor(text);
  const schema: ProtoSchema = {
    packageName: null,
    messages: [],
    enums: [],
  };

  while (true) {
    skipTrivia(cursor);
    if (cursor.done) return schema;

    if (consumeKeyword(cursor, "syntax")) {
      expectChar(cursor, "=");
      const value = readQuotedString(cursor);
      if (value !== "proto3") fail(cursor, `unsupported syntax '${value}', only 'proto3' is supported`);
      expectChar(cursor, ";");
      continue;
    }

    if (consumeKeyword(cursor, "package")) {
      schema.packageName = readDottedIdentifier(cursor, "package name");
      expectChar(cursor, ";");
      continue;
    }

    if (consumeKeyword(cursor, "import")) {
      // Skip the quoted path and trailing ';'.
      readQuotedString(cursor);
      expectChar(cursor, ";");
      continue;
    }

    if (consumeKeyword(cursor, "option")) {
      // Skip until terminating ';'.
      while (!cursor.done && cursor.peek() !== ";") cursor.advance();
      expectChar(cursor, ";");
      continue;
    }

    if (consumeKeyword(cursor, "message")) {
      schema.messages.push(parseMessage(cursor));
      continue;
    }

    if (consumeKeyword(cursor, "enum")) {
      schema.enums.push(parseEnum(cursor));
      continue;
    }

    fail(cursor, `unexpected token '${cursor.peek()}'`);
  }
}
