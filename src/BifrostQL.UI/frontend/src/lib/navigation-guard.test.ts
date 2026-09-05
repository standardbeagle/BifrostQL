// @vitest-environment jsdom
import { describe, it, expect, afterEach } from "vitest";
import { installNavigationGuard, isTopFrameNavigationAllowed } from "./navigation-guard";

describe("isTopFrameNavigationAllowed", () => {
  const local = "http://127.0.0.1:5000";

  it("allows same-origin absolute and relative hrefs", () => {
    expect(isTopFrameNavigationAllowed(`${local}/dashboards`, local)).toBe(true);
    expect(isTopFrameNavigationAllowed("/dashboards", local)).toBe(true);
    expect(isTopFrameNavigationAllowed("#fragment", local)).toBe(true);
  });

  it("rejects every foreign origin, including lookalike loopback ports", () => {
    expect(isTopFrameNavigationAllowed("https://evil.example/phish", local)).toBe(false);
    expect(isTopFrameNavigationAllowed("http://127.0.0.1:9999/other", local)).toBe(false);
    expect(isTopFrameNavigationAllowed("javascript:alert(1)", local)).toBe(false);
  });
});

describe("installNavigationGuard", () => {
  let teardown: (() => void) | null = null;
  afterEach(() => {
    teardown?.();
    teardown = null;
    document.body.innerHTML = "";
  });

  function click(href: string, target?: string): MouseEvent {
    const a = document.createElement("a");
    a.setAttribute("href", href);
    if (target) a.setAttribute("target", target);
    document.body.appendChild(a);
    const event = new MouseEvent("click", { bubbles: true, cancelable: true });
    a.dispatchEvent(event);
    return event;
  }

  it("cancels a top-frame navigation to a foreign origin", () => {
    teardown = installNavigationGuard();
    const event = click("https://evil.example/phish");
    expect(event.defaultPrevented).toBe(true);
  });

  it("leaves same-origin and _blank navigations alone", () => {
    teardown = installNavigationGuard();
    expect(click(`${window.location.origin}/dashboards`).defaultPrevented).toBe(false);
    expect(click("/dashboards").defaultPrevented).toBe(false);
    expect(click("https://evil.example/phish", "_blank").defaultPrevented).toBe(false);
  });
});
