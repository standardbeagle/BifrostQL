#!/usr/bin/env node
/**
 * @bifrostql/codegen CLI
 *
 * Reads a BifrostQL .proto schema and emits typed TypeScript interfaces — one
 * file per message + a barrel `index.ts` re-export.
 *
 * Usage:
 *   bifrostql-codegen --proto-file <path> --out <dir> [--grants <response.json>]
 *   bifrostql-codegen --endpoint ws://host/bifrost-ws --out <dir> [--header k=v ...]
 *
 * `--grants` adds `grants.ts` (the `Grant` union + `GRANTS` constant read from
 * a saved GraphQL response carrying `_policyGrants`) beside the message files
 * and re-exports it from the barrel. It always rides a proto source.
 *
 * NOTE: The server does not yet expose the generated .proto text via GraphQL.
 * `--endpoint` mode is wired through @bifrostql/binary-client and queries
 * `{ _proto }` (the planned field). Until the server-side exposure lands, prefer
 * `--proto-file` against a checked-in or out-of-band copy of the .proto.
 */

import { mkdir, readFile, writeFile } from "node:fs/promises";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { parseProto } from "./proto-parser.js";
import { emitSchema, grantCatalogue, type EmittedFile, type GrantResponse } from "./ts-emitter.js";

export interface CliOptions {
  endpoint: string | null;
  protoFile: string | null;
  grantsFile: string | null;
  outDir: string;
  headers: Record<string, string>;
  help: boolean;
}

/**
 * Parses argv (excluding `node` and the script path) into structured options.
 * Throws Error on unknown flags or missing values.
 */
export function parseArgs(argv: readonly string[]): CliOptions {
  const opts: CliOptions = {
    endpoint: null,
    protoFile: null,
    grantsFile: null,
    outDir: "./generated",
    headers: {},
    help: false,
  };

  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    switch (arg) {
      case "-h":
      case "--help":
        opts.help = true;
        break;
      case "--endpoint": {
        const value = argv[++i];
        if (!value) throw new Error("--endpoint requires a value");
        opts.endpoint = value;
        break;
      }
      case "--proto-file": {
        const value = argv[++i];
        if (!value) throw new Error("--proto-file requires a value");
        opts.protoFile = value;
        break;
      }
      case "--grants": {
        const value = argv[++i];
        if (!value) throw new Error("--grants requires a value");
        opts.grantsFile = value;
        break;
      }
      case "--out": {
        const value = argv[++i];
        if (!value) throw new Error("--out requires a value");
        opts.outDir = value;
        break;
      }
      case "--header": {
        const value = argv[++i];
        if (!value) throw new Error("--header requires a value (key=value)");
        const eq = value.indexOf("=");
        if (eq < 0) throw new Error(`--header value must be key=value, got '${value}'`);
        opts.headers[value.slice(0, eq)] = value.slice(eq + 1);
        break;
      }
      default:
        throw new Error(`unknown argument '${arg}'`);
    }
  }

  return opts;
}

/** The usage text printed by `--help`. */
export const USAGE = `@bifrostql/codegen — emit typed TypeScript interfaces from a BifrostQL .proto schema

Usage:
  bifrostql-codegen --proto-file <path> --out <dir> [--grants <path>]
  bifrostql-codegen --endpoint <ws-url> --out <dir> [--header key=value ...] [--grants <path>]

Options:
  --proto-file <path>    Path to a local .proto file (offline mode)
  --grants     <path>    Saved GraphQL response containing _policyGrants; adds
                         grants.ts (Grant union + GRANTS) to the barrel. Needs
                         --proto-file or --endpoint.
  --endpoint   <ws-url>  WebSocket endpoint of a running BifrostQL server
  --out        <dir>     Output directory for generated .ts files (default: ./generated)
  --header     <kv>      HTTP-style header for the WebSocket connection (repeatable)
  -h, --help             Show this help and exit

Notes:
  Until the server exposes its proto text via GraphQL, prefer --proto-file. Run
  the .NET test ProtoSchemaGeneratorTests against your model and capture the
  generated proto, or wait for the planned _proto root field.
`;

/**
 * Loads .proto text either from a local file or from a BifrostQL endpoint via
 * @bifrostql/binary-client. The binary-client import is dynamic so the CLI
 * stays usable without it for `--proto-file` flows.
 */
export async function loadProtoText(opts: CliOptions): Promise<string> {
  if (opts.protoFile) {
    return await readFile(resolve(opts.protoFile), "utf8");
  }

  if (!opts.endpoint) {
    throw new Error("either --proto-file or --endpoint must be provided");
  }

  // Lazy import so the CLI can run with only --proto-file even if the
  // binary-client peer dep isn't installed. The module specifier is held in a
  // variable so bundlers/analyzers (vite, esbuild) leave it as a runtime
  // resolution rather than failing when the peer isn't built yet.
  const binaryClientModule = "@bifrostql/binary-client";
  const mod = (await import(/* @vite-ignore */ binaryClientModule)) as {
    BifrostBinaryClient: new (options: {
      url: string;
      headers?: Record<string, string>;
    }) => {
      connect(): Promise<void>;
      query(text: string): Promise<{ data?: Record<string, unknown>; errors?: unknown }>;
      close(): void;
    };
  };

  // Forward any --header key=value pairs so authenticated endpoints (bearer
  // tokens, API keys) are reachable. Empty object → omit so the client uses its
  // no-headers path.
  const hasHeaders = Object.keys(opts.headers).length > 0;
  const client = new mod.BifrostBinaryClient({
    url: opts.endpoint,
    ...(hasHeaders ? { headers: opts.headers } : {}),
  });
  await client.connect();
  try {
    // The server doesn't expose `_proto` yet; this query is wired so the CLI
    // is ready the moment the field lands. Until then, `--endpoint` will
    // surface a GraphQL "field not found" error from the server, which is
    // exactly the actionable failure we want.
    const result = await client.query("{ _proto }");
    if (result.errors) {
      throw new Error(`server returned errors: ${JSON.stringify(result.errors)}`);
    }
    const proto = result.data?.["_proto"];
    if (typeof proto !== "string") {
      throw new Error("server response did not contain a string `_proto` field");
    }
    return proto;
  } finally {
    client.close();
  }
}

/**
 * Loads the saved `_policyGrants` response named by `--grants`. Codegen needs
 * the FULL projection: `_policyGrants` is every grant the model's policy
 * metadata names, but a caller lacking the admin role sees `_dbSchema`
 * narrowed to what it may read, so a non-admin `_grants` alongside a non-empty
 * catalogue is the tell that the catalogue may be short. Warn, do not fail —
 * a dedicated codegen identity may legitimately carry no admin role.
 */
export async function loadGrantResponse(
  grantsFile: string,
  stderr: (text: string) => void,
): Promise<GrantResponse> {
  const response = JSON.parse(await readFile(resolve(grantsFile), "utf8")) as GrantResponse;
  const grants = response.data?._grants;
  const hasAdmin = Array.isArray(grants) && grants.includes("admin");
  if (grantCatalogue(response).length > 0 && !hasAdmin) {
    stderr(
      "warning: grant response may be narrowed (_grants lacks admin); use admin credentials or a dedicated codegen identity\n",
    );
  }
  return response;
}

/** Writes every generated file under outDir, creating directories as needed. */
export async function writeFiles(outDir: string, files: readonly EmittedFile[]): Promise<void> {
  const absoluteOut = resolve(outDir);
  await mkdir(absoluteOut, { recursive: true });
  for (const file of files) {
    const target = join(absoluteOut, file.filename);
    await mkdir(dirname(target), { recursive: true });
    await writeFile(target, file.content, "utf8");
  }
}

/** Pretty-prints the summary table to stdout. */
export function printSummary(files: readonly EmittedFile[], outDir: string): void {
  const nameWidth = Math.max(8, ...files.map((f) => f.filename.length));
  const sizeWidth = 8;
  const header = `${"file".padEnd(nameWidth)}  ${"bytes".padStart(sizeWidth)}`;
  const sep = `${"-".repeat(nameWidth)}  ${"-".repeat(sizeWidth)}`;
  process.stdout.write(`Wrote ${files.length} file(s) to ${resolve(outDir)}\n`);
  process.stdout.write(header + "\n");
  process.stdout.write(sep + "\n");
  for (const file of files) {
    process.stdout.write(
      `${file.filename.padEnd(nameWidth)}  ${String(file.content.length).padStart(sizeWidth)}\n`,
    );
  }
}

/** Top-level entry point. Returns the process exit code. */
export async function main(argv: readonly string[]): Promise<number> {
  let opts: CliOptions;
  try {
    opts = parseArgs(argv);
  } catch (err) {
    process.stderr.write(`error: ${(err as Error).message}\n\n${USAGE}`);
    return 2;
  }

  if (opts.help) {
    process.stdout.write(USAGE);
    return 0;
  }

  if (!opts.protoFile && !opts.endpoint) {
    const missing = opts.grantsFile
      ? "--grants requires --proto-file or --endpoint (grants.ts is emitted beside the proto output)"
      : "either --proto-file or --endpoint must be provided";
    process.stderr.write(`error: ${missing}\n\n${USAGE}`);
    return 2;
  }

  try {
    const grants = opts.grantsFile
      ? await loadGrantResponse(opts.grantsFile, (text) => process.stderr.write(text))
      : undefined;
    const protoText = await loadProtoText(opts);
    const schema = parseProto(protoText);
    const files = emitSchema(schema, grants);
    await writeFiles(opts.outDir, files);
    printSummary(files, opts.outDir);
    return 0;
  } catch (err) {
    process.stderr.write(`error: ${(err as Error).message}\n`);
    return 1;
  }
}

// Run when invoked directly (not when imported by tests).
const isDirectInvocation = (() => {
  if (typeof process === "undefined" || !process.argv[1]) return false;
  try {
    return resolve(process.argv[1]) === resolve(fileURLToPath(import.meta.url));
  } catch {
    return false;
  }
})();

if (isDirectInvocation) {
  main(process.argv.slice(2)).then(
    (code) => process.exit(code),
    (err) => {
      process.stderr.write(`fatal: ${(err as Error).stack ?? String(err)}\n`);
      process.exit(1);
    },
  );
}
