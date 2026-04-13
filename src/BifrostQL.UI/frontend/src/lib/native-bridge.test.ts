/**
 * Unit tests for the Photino native bridge wrapper.
 *
 * These tests never touch a real Photino window. They install a fake
 * `window.external` that records outbound `sendMessage` calls and captures
 * the single `receiveMessage` dispatch callback, letting each test simulate
 * inbound messages synchronously. That mirrors how Photino behaves in
 * practice — it calls the callback once per host-to-webview message — and
 * it keeps the tests decoupled from JSDOM/webview plumbing.
 *
 * The bridge module is a singleton: first touch wires its dispatcher onto
 * `window.external.receiveMessage` and never re-registers. To get clean
 * isolation between tests we use `vi.resetModules()` + dynamic `await
 * import()` so each test gets a fresh module instance pointed at a fresh
 * fake external. This avoids leaking pending promises or event handlers
 * across cases.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Shape of the fake we install on globalThis for each test. Keeping this
// typed (rather than `any`) lets the assertions stay strict and catches
// accidental typos in the bridge code during the RED phase.
interface FakeExternal {
  sendMessage: ReturnType<typeof vi.fn>;
  receiveMessage: ReturnType<typeof vi.fn>;
  // Dispatch captured from the single receiveMessage registration. Tests
  // call this to simulate an inbound host-to-webview message.
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
  // Nuke the window so subsequent tests that don't install one see the
  // "missing external" code path.
  delete (globalThis as unknown as { window?: unknown }).window;
}

// Helper: grabs the JSON string that the bridge most recently handed to
// sendMessage and parses it. The bridge is expected to serialize to JSON
// so callers can extract the correlation id for reply simulation.
function lastSentEnvelope(
  fake: FakeExternal
): { id: string; kind: string; payload: unknown } {
  const calls = fake.sendMessage.mock.calls;
  expect(calls.length, "expected at least one sendMessage call").toBeGreaterThan(
    0
  );
  const raw = calls[calls.length - 1][0] as string;
  return JSON.parse(raw);
}

async function loadBridge(): Promise<typeof import("./native-bridge")> {
  vi.resetModules();
  return import("./native-bridge");
}

describe("native-bridge", () => {
  beforeEach(() => {
    clearExternal();
  });

  afterEach(() => {
    vi.useRealTimers();
    clearExternal();
  });

  it("resolves sendBridgeRequest with the host result payload", async () => {
    const fake = installFakeExternal();
    const { sendBridgeRequest } = await loadBridge();

    const pending = sendBridgeRequest<{ pong: boolean }>("ping", { x: 1 });

    // The bridge should have synchronously serialized the envelope and
    // called sendMessage exactly once with a JSON string matching the
    // documented wire shape.
    const envelope = lastSentEnvelope(fake);
    expect(envelope.kind).toBe("ping");
    expect(envelope.payload).toEqual({ x: 1 });
    expect(typeof envelope.id).toBe("string");
    expect(envelope.id.length).toBeGreaterThan(0);

    // Simulate the host writing back a result with the matching id.
    fake.dispatch!(
      JSON.stringify({
        id: envelope.id,
        kind: "result",
        payload: { pong: true },
      })
    );

    await expect(pending).resolves.toEqual({ pong: true });
  });

  it("rejects sendBridgeRequest with BridgeError on error envelope", async () => {
    const fake = installFakeExternal();
    const { sendBridgeRequest, BridgeError } = await loadBridge();

    const pending = sendBridgeRequest("boom");
    const envelope = lastSentEnvelope(fake);

    fake.dispatch!(
      JSON.stringify({
        id: envelope.id,
        kind: "error",
        payload: { message: "kaboom" },
      })
    );

    await expect(pending).rejects.toBeInstanceOf(BridgeError);
    await expect(pending).rejects.toMatchObject({
      kind: "error",
      message: "kaboom",
    });
  });

  it("rejects sendBridgeRequest with a timeout BridgeError when no response arrives", async () => {
    vi.useFakeTimers();
    installFakeExternal();
    const { sendBridgeRequest, BridgeError } = await loadBridge();

    const pending = sendBridgeRequest("slow", undefined, { timeoutMs: 50 });
    // Attach a catch synchronously so the unhandled rejection that fires
    // when we advance timers isn't treated as a test-level failure.
    const settled = pending.catch((err) => err);

    vi.advanceTimersByTime(100);

    const err = await settled;
    expect(err).toBeInstanceOf(BridgeError);
    expect((err as InstanceType<typeof BridgeError>).kind).toBe("timeout");
  });

  it("delivers unsolicited host events to onBridgeEvent subscribers", async () => {
    const fake = installFakeExternal();
    const { onBridgeEvent } = await loadBridge();

    const handler = vi.fn();
    onBridgeEvent("credential-saved", handler);

    fake.dispatch!(
      JSON.stringify({
        id: "server-generated-1",
        kind: "credential-saved",
        payload: { name: "db-1" },
      })
    );

    expect(handler).toHaveBeenCalledTimes(1);
    expect(handler).toHaveBeenCalledWith({ name: "db-1" });
  });

  it("stops delivering events after the returned unsubscribe function is called", async () => {
    const fake = installFakeExternal();
    const { onBridgeEvent } = await loadBridge();

    const handler = vi.fn();
    const unsubscribe = onBridgeEvent("tick", handler);

    fake.dispatch!(
      JSON.stringify({ id: "a", kind: "tick", payload: 1 })
    );
    unsubscribe();
    fake.dispatch!(
      JSON.stringify({ id: "b", kind: "tick", payload: 2 })
    );

    expect(handler).toHaveBeenCalledTimes(1);
    expect(handler).toHaveBeenCalledWith(1);
  });

  it("reports isBridgeAvailable=false when window.external is missing", async () => {
    // Intentionally do NOT install a fake — the bridge should detect the
    // absence and report unavailable rather than throwing at import time.
    const { isBridgeAvailable } = await loadBridge();
    expect(isBridgeAvailable()).toBe(false);
  });

  it("tolerates malformed inbound JSON and still handles the next valid message", async () => {
    const fake = installFakeExternal();
    const { sendBridgeRequest } = await loadBridge();

    const pending = sendBridgeRequest<{ ok: true }>("resilient");
    const envelope = lastSentEnvelope(fake);

    // First inbound is garbage — the dispatcher must not throw or tear
    // down the subscription.
    expect(() => fake.dispatch!("this is not json")).not.toThrow();

    // Second inbound is a valid reply with the matching id.
    fake.dispatch!(
      JSON.stringify({
        id: envelope.id,
        kind: "result",
        payload: { ok: true },
      })
    );

    await expect(pending).resolves.toEqual({ ok: true });
  });

  it("routes concurrent requests to their own deferreds without cross-talk", async () => {
    const fake = installFakeExternal();
    const { sendBridgeRequest } = await loadBridge();

    const first = sendBridgeRequest<number>("calc", { n: 1 });
    const firstEnv = lastSentEnvelope(fake);
    const second = sendBridgeRequest<number>("calc", { n: 2 });
    const secondEnv = lastSentEnvelope(fake);

    expect(firstEnv.id).not.toBe(secondEnv.id);

    // Reply in reversed order to catch any FIFO assumption.
    fake.dispatch!(
      JSON.stringify({ id: secondEnv.id, kind: "result", payload: 200 })
    );
    fake.dispatch!(
      JSON.stringify({ id: firstEnv.id, kind: "result", payload: 100 })
    );

    await expect(first).resolves.toBe(100);
    await expect(second).resolves.toBe(200);
  });
});
