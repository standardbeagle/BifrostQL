/**
 * Top-frame navigation guard for the desktop shell (LOW finding: NativeBridgeHost
 * dispatches every WebMessageReceived and DesktopShell placed no navigation
 * restriction on the window). Photino 4.x exposes no navigation event, managed
 * or native, so the guard lives in the page the webview loads: any anchor-driven
 * top-frame navigation to an origin other than the one the shell was served
 * from (the loopback UI host) is cancelled. Sub-frame and same-origin
 * navigations are untouched.
 */
export function isTopFrameNavigationAllowed(href: string, localOrigin: string): boolean {
  let url: URL;
  try {
    url = new URL(href, localOrigin);
  } catch {
    return false;
  }
  return url.origin === localOrigin;
}

/**
 * Installs the guard on `document` (capture phase, so it runs before any app
 * handler). Returns the teardown function.
 */
export function installNavigationGuard(doc: Document = document): () => void {
  const localOrigin = doc.defaultView?.location.origin ?? window.location.origin;

  const onClick = (event: MouseEvent) => {
    const anchor = (event.target as Element | null)?.closest?.("a[href]");
    if (!anchor) return;

    // Only top-frame navigation is restricted: _blank/subframe targets hand the
    // decision to the webview's own policy, not ours.
    const target = anchor.getAttribute("target");
    if (target === "_blank") return;

    const href = anchor.getAttribute("href");
    if (!href || !isTopFrameNavigationAllowed(href, localOrigin)) {
      event.preventDefault();
    }
  };

  doc.addEventListener("click", onClick, true);
  return () => doc.removeEventListener("click", onClick, true);
}
