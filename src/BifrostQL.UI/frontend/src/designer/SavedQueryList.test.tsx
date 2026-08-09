// @vitest-environment jsdom
/**
 * The Saved Queries rail's load path, driven through the REAL saved-object
 * client with only `fetch` stubbed — so the browser's own JSON parsing is what
 * fails, exactly as it did in the app.
 *
 * Observed live in the desktop app: opening the visual query designer painted a
 * red "The string did not match the expected pattern." in this rail on mount,
 * with no user action. That is WebKit's SyntaxError text for `Response.json()`
 * on a non-JSON body — the host was not serving `/_saved-objects`, so the
 * request fell through to the SPA's index.html and the rail rendered the
 * browser's raw exception message. The endpoint is registered separately; these
 * tests pin that no raw browser parse error can reach the rail again, whatever
 * the transport returns.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { SavedQueryList } from "./SavedQueryList";
import { describeSavedQueryLoadFailure, isAbortError } from "./saved-query-store";

const fetchMock = vi.fn();

beforeEach(() => {
  vi.stubGlobal("fetch", fetchMock);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  fetchMock.mockReset();
});

function json(body: unknown) {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { "Content-Type": "application/json" },
  });
}

/** What the SPA fallback answered: 200 OK, but index.html. */
function spaFallback() {
  return new Response("<!doctype html><html><body><div id=\"root\"></div></body></html>", {
    status: 200,
    headers: { "Content-Type": "text/html" },
  });
}

function saved(id: string, name: string) {
  return { id, type: "query", name, definition: {}, version: 1 };
}

describe("SavedQueryList", () => {
  it("renders an empty state, not an error, when the store holds nothing", async () => {
    fetchMock.mockResolvedValue(json([]));

    render(<SavedQueryList activeId={null} reloadToken={0} onOpen={() => {}} />);

    await waitFor(() => expect(screen.getByText(/No saved queries yet/i)).toBeTruthy());
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("never shows the browser's raw JSON-parse error when the SPA fallback answers", async () => {
    fetchMock.mockResolvedValue(spaFallback());

    render(<SavedQueryList activeId={null} reloadToken={0} onOpen={() => {}} />);

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).not.toMatch(/did not match the expected pattern|Unexpected token|JSON Parse error/i);
    expect(alert.textContent).toMatch(/saved quer/i);
    expect(alert.textContent).toMatch(/not saved-query data/i);
  });

  it("names saved queries in a transport failure and keeps the server's reason", async () => {
    fetchMock.mockResolvedValue(
      new Response("{}", { status: 503, statusText: "Service Unavailable" }),
    );

    render(<SavedQueryList activeId={null} reloadToken={0} onOpen={() => {}} />);

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toMatch(/saved quer/i);
    expect(alert.textContent).toContain("503");
  });

  it("still lists the queries that loaded", async () => {
    fetchMock.mockResolvedValue(json([saved("query:b", "Beta"), saved("query:a", "Alpha")]));

    render(<SavedQueryList activeId={null} reloadToken={0} onOpen={() => {}} />);

    await waitFor(() => expect(screen.getByText("Alpha")).toBeTruthy());
    expect(screen.getByText("Beta")).toBeTruthy();
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("keeps the good entries when one stored entry is malformed", async () => {
    fetchMock.mockResolvedValue(json([saved("query:a", "Alpha"), { id: "query:broken" }]));

    render(<SavedQueryList activeId={null} reloadToken={0} onOpen={() => {}} />);

    await waitFor(() => expect(screen.getByText("Alpha")).toBeTruthy());
  });
});

describe("describeSavedQueryLoadFailure", () => {
  it("replaces a browser parse error rather than forwarding it", () => {
    const webkit = describeSavedQueryLoadFailure(
      new SyntaxError("The string did not match the expected pattern."),
    );
    expect(webkit).not.toContain("did not match the expected pattern");
    expect(webkit).toMatch(/not saved-query data/i);

    const chromium = describeSavedQueryLoadFailure(new SyntaxError("Unexpected token '<'"));
    expect(chromium).toBe(webkit);
  });

  it("keeps a server-authored reason verbatim so the failure is not swallowed", () => {
    const message = describeSavedQueryLoadFailure(
      new Error("Failed to list saved objects: 503 Service Unavailable"),
    );
    expect(message).toContain("Failed to list saved objects: 503 Service Unavailable");
    expect(message).toMatch(/saved quer/i);
  });
});

describe("isAbortError", () => {
  it("recognises an aborted request so it is not reported as a failure", () => {
    expect(isAbortError(new DOMException("aborted", "AbortError"))).toBe(true);
    expect(isAbortError(new Error("boom"))).toBe(false);
  });
});
