import { beforeEach, describe, expect, it, vi } from "vitest";

/**
 * The probe that decides whether the desktop-only panes are reachable over the
 * opt-in HTTP transport (`--enable-http-bridge`).
 *
 * The trap these pin: the UI host serves the SPA from a catch-all fallback, so
 * GET on ANY unknown path answers 200 with index.html. A probe that trusted the
 * status code would report the bridge as present on every host — the panes would
 * appear and every call behind them would fail. The endpoint's own JSON marker is
 * the only trustworthy signal.
 */

async function loadBridge() {
  vi.resetModules();
  return import("./native-bridge");
}

function mockFetch(response: { ok: boolean; json?: () => Promise<unknown> }) {
  const fetchMock = vi.fn().mockResolvedValue({
    ok: response.ok,
    json: response.json ?? (() => Promise.resolve(null)),
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

describe("probeBridgeAvailability", () => {
  beforeEach(() => {
    vi.unstubAllGlobals();
    // No Photino: the native probe must answer false so the HTTP path is reached.
    (globalThis as unknown as { window: unknown }).window = {};
  });

  it("reports available when the endpoint returns its marker", async () => {
    mockFetch({ ok: true, json: () => Promise.resolve({ enabled: true }) });
    const { probeBridgeAvailability, isAnyBridgeAvailable } = await loadBridge();

    await expect(probeBridgeAvailability()).resolves.toBe(true);
    expect(isAnyBridgeAvailable()).toBe(true);
  });

  it("reports unavailable when the SPA fallback answers 200 with HTML", async () => {
    // The regression: a 200 whose body is not the marker. json() rejects on HTML.
    mockFetch({ ok: true, json: () => Promise.reject(new Error("not json")) });
    const { probeBridgeAvailability, isAnyBridgeAvailable } = await loadBridge();

    await expect(probeBridgeAvailability()).resolves.toBe(false);
    expect(isAnyBridgeAvailable()).toBe(false);
  });

  it("reports unavailable when a 200 carries JSON without the marker", async () => {
    mockFetch({ ok: true, json: () => Promise.resolve({ something: "else" }) });
    const { probeBridgeAvailability } = await loadBridge();

    await expect(probeBridgeAvailability()).resolves.toBe(false);
  });

  it("reports unavailable when the route does not exist", async () => {
    mockFetch({ ok: false });
    const { probeBridgeAvailability } = await loadBridge();

    await expect(probeBridgeAvailability()).resolves.toBe(false);
  });

  it("reports unavailable when the probe request throws", async () => {
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new Error("offline")));
    const { probeBridgeAvailability } = await loadBridge();

    await expect(probeBridgeAvailability()).resolves.toBe(false);
  });

  it("probes once and caches the answer", async () => {
    const fetchMock = mockFetch({ ok: true, json: () => Promise.resolve({ enabled: true }) });
    const { probeBridgeAvailability } = await loadBridge();

    await probeBridgeAvailability();
    await probeBridgeAvailability();

    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("does not probe at all when the native bridge is present", async () => {
    const fetchMock = mockFetch({ ok: true, json: () => Promise.resolve({ enabled: true }) });
    (globalThis as unknown as { window: unknown }).window = {
      external: { sendMessage: () => {}, receiveMessage: () => {} },
    };
    const { probeBridgeAvailability } = await loadBridge();

    // Photino always wins: the desktop app must not depend on the host having
    // opened a socket for the bridge at all.
    await expect(probeBridgeAvailability()).resolves.toBe(true);
    expect(fetchMock).not.toHaveBeenCalled();
  });
});
