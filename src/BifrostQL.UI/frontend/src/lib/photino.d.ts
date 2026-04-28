/**
 * Ambient type declarations for the Photino-injected native bridge.
 *
 * Photino exposes two functions on `window.external` inside the webview:
 *
 * - `sendMessage(message)` — sends a string to the C# host. The host receives
 *   it via the `WebMessageReceived` event on the `PhotinoWindow`.
 *
 * - `receiveMessage(callback)` — registers a single dispatch callback that
 *   Photino invokes for every string the host pushes back via
 *   `PhotinoWindow.SendWebMessage`. Only one callback is expected at a time;
 *   our bridge wraps this with a fan-out dispatcher.
 *
 * Both functions deal in strings; the bridge layers JSON + correlation IDs
 * on top. The declarations live in their own `.d.ts` so they augment the
 * global `Window`/`External` types without polluting the runtime bundle.
 */
declare global {
  interface External {
    sendMessage(message: string): void;
    receiveMessage(callback: (message: string) => void): void;
  }

  interface Window {
    external: External;
  }
}

export {};
