/**
 * Unit tests for the credential-prompt wrapper.
 *
 * These tests follow the same fake-window.external pattern as
 * native-bridge.test.ts: install a stub that captures the single
 * `receiveMessage` dispatch callback, invoke the wrapper, and simulate
 * inbound messages synchronously. The goal is to exercise the wrapper's
 * cancel/error discrimination logic without touching a real Photino child
 * window.
 *
 * As with native-bridge.test.ts, both the bridge and the wrapper are
 * module-scoped singletons, so we use `vi.resetModules()` + dynamic import
 * to get a fresh instance per case.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

interface FakeExternal {
  sendMessage: ReturnType<typeof vi.fn>;
  receiveMessage: ReturnType<typeof vi.fn>;
  dispatch: ((json: string) => void) | null;
}

function installFakeExternal(): FakeExternal {
  const fake: FakeExternal = {
    sendMessage: vi.fn(),
    receiveMessage: vi.fn(),
    dispatch: null,
  };
  fake.receiveMessage.mockImplementation((cb: (json: string) => void) => {
    fake.dispatch = cb;
  });
  (globalThis as unknown as { window: { external: FakeExternal } }).window = {
    external: fake,
  };
  return fake;
}

function clearExternal(): void {
  delete (globalThis as unknown as { window?: unknown }).window;
}

function lastSentEnvelope(
  fake: FakeExternal
): { id: string; kind: string; payload: unknown } {
  const calls = fake.sendMessage.mock.calls;
  expect(
    calls.length,
    "expected at least one sendMessage call"
  ).toBeGreaterThan(0);
  const raw = calls[calls.length - 1][0] as string;
  return JSON.parse(raw);
}

async function loadModule(): Promise<typeof import("./credential-prompt")> {
  vi.resetModules();
  return import("./credential-prompt");
}

describe("credential-prompt", () => {
  beforeEach(() => {
    clearExternal();
  });

  afterEach(() => {
    vi.useRealTimers();
    clearExternal();
  });

  it("resolves with the host result on the happy path and sends request-credential kind", async () => {
    const fake = installFakeExternal();
    const { requestCredential } = await loadModule();

    const info = {
      vaultName: "prod-db",
      provider: "postgres",
      host: "db.example.com",
      port: 5432,
      database: "app",
      username: "alice",
      ssl: true,
    };

    const pending = requestCredential(info);

    // The wrapper must route through sendBridgeRequest and produce a
    // wire envelope with kind === "request-credential" carrying the full
    // ConnectionInfo payload. If the wrapper mangles the payload shape the
    // host handler will reject on missing fields, so the contract is
    // load-bearing and asserted verbatim here.
    const envelope = lastSentEnvelope(fake);
    expect(envelope.kind).toBe("request-credential");
    expect(envelope.payload).toEqual(info);

    fake.dispatch!(
      JSON.stringify({
        id: envelope.id,
        kind: "result",
        payload: { saved: true, name: "prod-db" },
      })
    );

    await expect(pending).resolves.toEqual({ saved: true, name: "prod-db" });
  });

  it("maps a cancel error from the host into CredentialCancelledError", async () => {
    const fake = installFakeExternal();
    const { requestCredential, CredentialCancelledError } = await loadModule();

    const pending = requestCredential({
      vaultName: "scratch",
      provider: "sqlite",
    });
    // Attach catch synchronously so the unhandled rejection that fires
    // when the dispatcher rejects the promise isn't treated as a test
    // failure by vitest's strict unhandled-rejection mode.
    const settled = pending.catch((err) => err);

    const envelope = lastSentEnvelope(fake);
    // The child window exits with Cancelled() -> host wraps into a
    // BridgeError with a message containing the word "cancel" (any
    // casing). The wrapper must detect this and re-throw as
    // CredentialCancelledError so the UI can suppress it silently.
    fake.dispatch!(
      JSON.stringify({
        id: envelope.id,
        kind: "error",
        payload: { message: "User cancelled the credential prompt" },
      })
    );

    const err = await settled;
    expect(err).toBeInstanceOf(CredentialCancelledError);
  });

  it("propagates non-cancel bridge errors as BridgeError so the UI can surface them", async () => {
    const fake = installFakeExternal();
    const { requestCredential } = await loadModule();
    const { BridgeError } = await import("./native-bridge");

    const pending = requestCredential({
      vaultName: "prod",
      provider: "postgres",
    });
    const settled = pending.catch((err) => err);

    const envelope = lastSentEnvelope(fake);
    fake.dispatch!(
      JSON.stringify({
        id: envelope.id,
        kind: "error",
        payload: { message: "vault write failed: disk full" },
      })
    );

    const err = await settled;
    expect(err).toBeInstanceOf(BridgeError);
    expect((err as Error).message).toMatch(/disk full/);
  });

  it("rejects with a descriptive error when no native bridge is available", async () => {
    // Deliberately skip installFakeExternal — the wrapper must detect the
    // missing bridge up-front and reject with guidance that points the
    // user at the CLI vault-add flow instead of the browser form.
    const { requestCredential } = await loadModule();

    await expect(
      requestCredential({ vaultName: "x", provider: "postgres" })
    ).rejects.toThrow(/bridge/i);
  });

  it("passes all structured ConnectionInfo fields through to the wire payload", async () => {
    // Smoke test for the payload shape: we want every optional field the
    // C# host reads (host/port/database/username/ssl) to be preserved
    // verbatim. A silent drop here would leave the vault entry with
    // missing fields and the downstream /api/vault/connect call would
    // fail with a confusing "unknown server" error that's hard to trace
    // back to the bridge wrapper.
    const fake = installFakeExternal();
    const { requestCredential } = await loadModule();

    const pending = requestCredential({
      vaultName: "qa-mysql",
      provider: "mysql",
      host: "10.0.0.5",
      port: 3306,
      database: "qa",
      username: "qa-user",
      ssl: false,
    });
    // Resolve immediately so the test doesn't leak a pending promise.
    const envelope = lastSentEnvelope(fake);
    fake.dispatch!(
      JSON.stringify({
        id: envelope.id,
        kind: "result",
        payload: { saved: true, name: "qa-mysql" },
      })
    );
    await pending;

    const payload = envelope.payload as Record<string, unknown>;
    expect(payload.vaultName).toBe("qa-mysql");
    expect(payload.provider).toBe("mysql");
    expect(payload.host).toBe("10.0.0.5");
    expect(payload.port).toBe(3306);
    expect(payload.database).toBe("qa");
    expect(payload.username).toBe("qa-user");
    expect(payload.ssl).toBe(false);
  });
});
