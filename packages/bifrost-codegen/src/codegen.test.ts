import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { parseProto } from "./proto-parser.js";
import { emitEnum, emitMessage, emitSchema, mapProtoType } from "./ts-emitter.js";
import { parseArgs, USAGE, writeFiles } from "./cli.js";

const TWO_MESSAGE_SCHEMA = `
syntax = "proto3";

package bifrostql;

// A user row pulled from the users table.
message UserRow {
  int32 id = 1;
  string name = 2;
  optional string email = 3;
  bool isActive = 4;
  /* Friend ids in adjacency list form */
  repeated int64 friendIds = 5;
}

message OrderRow {
  int32 orderId = 1;
  optional double total = 2;
  bytes payload = 3;
  Status status = 4;
}

enum Status {
  PENDING = 0;
  SHIPPED = 1;
  DELIVERED = 2;
}
`;

describe("proto-parser", () => {
  it("parses a two-message schema with comments and primitives", () => {
    const schema = parseProto(TWO_MESSAGE_SCHEMA);

    expect(schema.packageName).toBe("bifrostql");
    expect(schema.messages).toHaveLength(2);
    expect(schema.enums).toHaveLength(1);

    const user = schema.messages[0]!;
    expect(user.name).toBe("UserRow");
    expect(user.fields).toEqual([
      { name: "id", type: "int32", repeated: false, optional: false, number: 1 },
      { name: "name", type: "string", repeated: false, optional: false, number: 2 },
      { name: "email", type: "string", repeated: false, optional: true, number: 3 },
      { name: "isActive", type: "bool", repeated: false, optional: false, number: 4 },
      { name: "friendIds", type: "int64", repeated: true, optional: false, number: 5 },
    ]);

    const order = schema.messages[1]!;
    expect(order.name).toBe("OrderRow");
    expect(order.fields.map((f) => f.type)).toEqual(["int32", "double", "bytes", "Status"]);
    expect(order.fields[1]!.optional).toBe(true);

    expect(schema.enums[0]!).toEqual({
      name: "Status",
      values: [
        { name: "PENDING", number: 0 },
        { name: "SHIPPED", number: 1 },
        { name: "DELIVERED", number: 2 },
      ],
    });
  });

  it("flattens oneof fields into the parent message", () => {
    const schema = parseProto(`
      syntax = "proto3";
      message Envelope {
        string table = 1;
        oneof payload {
          UserRow user_row = 2;
          OrderRow order_row = 3;
        }
      }
    `);

    expect(schema.messages[0]!.fields.map((f) => f.name)).toEqual([
      "table",
      "user_row",
      "order_row",
    ]);
    expect(schema.messages[0]!.fields[1]!.type).toBe("UserRow");
  });

  it("tolerates field options and reserved declarations", () => {
    const schema = parseProto(`
      syntax = "proto3";
      message Thing {
        reserved 4, 5;
        int32 id = 1 [deprecated = true];
      }
    `);
    expect(schema.messages[0]!.fields).toHaveLength(1);
    expect(schema.messages[0]!.fields[0]!.number).toBe(1);
  });

  it("throws with line/column information on bad syntax", () => {
    expect(() =>
      parseProto(`
        syntax = "proto3";
        message Bad {
          int32 = 1;
        }
      `),
    ).toThrowError(/line \d+, column \d+/);
  });

  it("rejects non-proto3 syntax", () => {
    expect(() => parseProto('syntax = "proto2";')).toThrowError(/proto3/);
  });

  it("ignores import and option statements", () => {
    const schema = parseProto(`
      syntax = "proto3";
      import "google/protobuf/empty.proto";
      option go_package = "example/foo";
      message X { int32 a = 1; }
    `);
    expect(schema.messages).toHaveLength(1);
  });
});

describe("ts-emitter", () => {
  it("maps every BifrostQL-emitted scalar type", () => {
    expect(mapProtoType("int32")).toBe("number");
    expect(mapProtoType("uint32")).toBe("number");
    expect(mapProtoType("sint32")).toBe("number");
    expect(mapProtoType("fixed32")).toBe("number");
    expect(mapProtoType("sfixed32")).toBe("number");
    expect(mapProtoType("int64")).toBe("bigint");
    expect(mapProtoType("uint64")).toBe("bigint");
    expect(mapProtoType("sint64")).toBe("bigint");
    expect(mapProtoType("fixed64")).toBe("bigint");
    expect(mapProtoType("sfixed64")).toBe("bigint");
    expect(mapProtoType("float")).toBe("number");
    expect(mapProtoType("double")).toBe("number");
    expect(mapProtoType("string")).toBe("string");
    expect(mapProtoType("bool")).toBe("boolean");
    expect(mapProtoType("bytes")).toBe("Uint8Array");
  });

  it("passes message references through unchanged", () => {
    expect(mapProtoType("UserRow")).toBe("UserRow");
  });

  it("emits a strict TypeScript interface for a message", () => {
    const schema = parseProto(TWO_MESSAGE_SCHEMA);
    const text = emitMessage(schema.messages[0]!);
    expect(text).toBe(
      [
        "export interface UserRow {",
        "  id: number;",
        "  name: string;",
        "  email: string | null;",
        "  isActive: boolean;",
        "  friendIds: bigint[];",
        "}",
        "",
      ].join("\n"),
    );
  });

  it("uses bigint for repeated int64", () => {
    const schema = parseProto(TWO_MESSAGE_SCHEMA);
    // Sanity-check the mapping for the friendIds field above.
    const user = schema.messages[0]!;
    const friendIds = user.fields.find((f) => f.name === "friendIds")!;
    expect(friendIds.type).toBe("int64");
    expect(mapProtoType(friendIds.type)).toBe("bigint");
  });

  it("emits snake_case field names as plain JS identifiers", () => {
    const schema = parseProto(`
      syntax = "proto3";
      message Weird {
        string valid_snake = 1;
      }
    `);
    // Snake_case is a valid JS identifier, so it should NOT be quoted.
    const text = emitMessage(schema.messages[0]!);
    expect(text).toContain("valid_snake: string;");
  });

  it("emits a const enum mirror", () => {
    const schema = parseProto(TWO_MESSAGE_SCHEMA);
    const text = emitEnum(schema.enums[0]!);
    expect(text).toBe(
      [
        "export const enum Status {",
        "  PENDING = 0,",
        "  SHIPPED = 1,",
        "  DELIVERED = 2,",
        "}",
        "",
      ].join("\n"),
    );
  });

  it("emits one file per message + per enum + an index barrel", () => {
    const schema = parseProto(TWO_MESSAGE_SCHEMA);
    const files = emitSchema(schema);

    const filenames = files.map((f) => f.filename).sort();
    expect(filenames).toEqual(["OrderRow.ts", "Status.ts", "UserRow.ts", "index.ts"].sort());

    for (const file of files) {
      expect(file.content.startsWith("// AUTO-GENERATED by @bifrostql/codegen")).toBe(true);
    }

    const index = files.find((f) => f.filename === "index.ts")!;
    expect(index.content).toContain('export type { UserRow } from "./UserRow.js";');
    expect(index.content).toContain('export type { OrderRow } from "./OrderRow.js";');
    expect(index.content).toContain('export { Status } from "./Status.js";');
  });
});

describe("cli argv parsing", () => {
  it("parses --proto-file + --out", () => {
    const opts = parseArgs(["--proto-file", "schema.proto", "--out", "./gen"]);
    expect(opts.protoFile).toBe("schema.proto");
    expect(opts.outDir).toBe("./gen");
    expect(opts.endpoint).toBeNull();
    expect(opts.help).toBe(false);
  });

  it("parses --endpoint + repeatable --header", () => {
    const opts = parseArgs([
      "--endpoint",
      "ws://localhost:5000/bifrost-ws",
      "--header",
      "Authorization=Bearer token",
      "--header",
      "X-Tenant=acme",
      "--out",
      "./gen",
    ]);
    expect(opts.endpoint).toBe("ws://localhost:5000/bifrost-ws");
    expect(opts.headers).toEqual({
      Authorization: "Bearer token",
      "X-Tenant": "acme",
    });
  });

  it("recognises --help and -h", () => {
    expect(parseArgs(["--help"]).help).toBe(true);
    expect(parseArgs(["-h"]).help).toBe(true);
  });

  it("rejects unknown flags", () => {
    expect(() => parseArgs(["--nope"])).toThrowError(/unknown argument/);
  });

  it("rejects malformed --header values", () => {
    expect(() => parseArgs(["--header", "no-equals"])).toThrowError(/key=value/);
  });

  it("requires a value for --out", () => {
    expect(() => parseArgs(["--out"])).toThrowError(/--out requires a value/);
  });

  it("USAGE mentions both modes", () => {
    expect(USAGE).toContain("--proto-file");
    expect(USAGE).toContain("--endpoint");
  });
});

describe("writeFiles", () => {
  it("writes every emitted file to disk under the target directory", async () => {
    const dir = await mkdtemp(join(tmpdir(), "bifrostql-codegen-test-"));
    try {
      const schema = parseProto(TWO_MESSAGE_SCHEMA);
      const files = emitSchema(schema);
      await writeFiles(dir, files);

      const userContent = await readFile(join(dir, "UserRow.ts"), "utf8");
      expect(userContent).toContain("export interface UserRow");
      const indexContent = await readFile(join(dir, "index.ts"), "utf8");
      expect(indexContent).toContain('UserRow } from "./UserRow.js"');
    } finally {
      await rm(dir, { recursive: true, force: true });
    }
  });
});
