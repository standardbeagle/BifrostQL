/**
 * Credential-prompt wrapper: thin contract layer over the Photino native
 * bridge for the one request kind that carries password material.
 *
 * Why a dedicated wrapper instead of calling `sendBridgeRequest` directly:
 *
 *   1. **Cancel vs error discrimination.** The C# child window returns a
 *      `CredentialResult.Cancelled()` when the user hits Escape / Cancel.
 *      The host scrubs that into a `BridgeError` with a message containing
 *      "cancel". Every call site needs to swallow that cancel silently
 *      (no toast, no error state) while still surfacing real errors. Doing
 *      that discrimination once here keeps the React components dumb.
 *
 *   2. **Timeout tuning.** The default bridge timeout is 10s, but a human
 *      typing a password may take much longer. We bump the per-request
 *      timeout to 5 minutes so slow typers aren't punished with a
 *      `BridgeError("timeout", ...)` mid-entry.
 *
 *   3. **Explicit bridge-availability error.** The default
 *      `BridgeError("unavailable", ...)` message is generic. For the
 *      credential path specifically we want to point the user at the CLI
 *      alternative (`bifrostui vault add`) so they aren't left stranded in
 *      a browser tab wondering why nothing happens.
 *
 * SECURITY: no password ever touches this file. The ConnectionInfo carries
 * only non-sensitive metadata (host/port/db/username/ssl). The host opens
 * an isolated child window that collects the password and writes the vault
 * entry server-side; the renderer only sees `{saved, name}` on success.
 */

import {
  sendBridgeRequest,
  BridgeError,
  isBridgeAvailable,
} from "./native-bridge";

/**
 * Non-sensitive connection metadata passed to the credential prompt. The
 * password is deliberately NOT part of this shape — it's collected by the
 * isolated child window and never crosses this module.
 */
export interface ConnectionInfo {
  /** The vault entry name under which the credential will be persisted. */
  vaultName: string;
  /** Database provider: "postgres", "mysql", "sqlserver", "sqlite". */
  provider: string;
  host?: string;
  port?: number;
  database?: string;
  username?: string;
  ssl?: boolean;
}

/**
 * Shape returned by the host on a successful save. `saved` is always
 * `true` in this branch — a user-cancelled prompt rejects the promise
 * with `CredentialCancelledError` instead of resolving with
 * `{saved: false}`, which keeps the call-site flow straightforward
 * (happy path = promise resolves, user bailed = catch the cancel error).
 */
export interface CredentialPromptResult {
  saved: boolean;
  name: string;
}

/**
 * Thrown when the user hits Cancel / Escape / closes the prompt window.
 * Call sites should catch this and silently return to their prior state
 * rather than showing an error toast.
 */
export class CredentialCancelledError extends Error {
  constructor() {
    super("Credential entry cancelled");
    this.name = "CredentialCancelledError";
  }
}

// Five-minute deadline — long enough for a user to locate a password
// manager entry, short enough that a dead host eventually surfaces as a
// failed promise instead of hanging the UI forever.
const CREDENTIAL_PROMPT_TIMEOUT_MS = 5 * 60 * 1000;

// Substring test used to recognise a "user cancelled" error envelope.
// The C# side throws `OperationCanceledException` (or our own cancel
// exception) which the bridge host scrubs into a message containing
// the word "cancel". Matching case-insensitively is intentional — we
// don't want to couple this check to the exact wording on the C# side.
const CANCEL_MESSAGE_PATTERN = /cancel/i;

/**
 * Opens the Photino credential prompt child window to collect a password
 * for the named vault entry and have the host persist it.
 *
 * @param info Non-sensitive connection metadata. The host constructs a
 *   `VaultServer` record from these fields plus the password it collects
 *   in the child window, then writes it via `VaultStore.Save` before
 *   returning to this caller.
 *
 * @returns Resolves with `{saved, name}` on success. The caller should
 *   refetch `/api/vault/servers` and then POST `/api/vault/connect` with
 *   the returned name to finish activating the connection.
 *
 * @throws `CredentialCancelledError` if the user cancels the prompt.
 *   Call sites should catch-and-ignore this to preserve prior UI state.
 * @throws `BridgeError` on any other bridge or host failure. The message
 *   is safe to display (the host scrubs secrets via `SecretScrubber`).
 * @throws A generic `Error` with guidance if the native bridge is not
 *   available — typically because the UI is loaded in a plain browser
 *   rather than via `bifrostui` (e.g. during `pnpm dev`).
 */
export async function requestCredential(
  info: ConnectionInfo
): Promise<CredentialPromptResult> {
  if (!isBridgeAvailable()) {
    throw new Error(
      "Native bridge unavailable. Run the desktop app (bifrostui) instead of a plain browser, or add credentials via `bifrostui vault add` from the CLI."
    );
  }

  try {
    return await sendBridgeRequest<CredentialPromptResult>(
      "request-credential",
      info,
      { timeoutMs: CREDENTIAL_PROMPT_TIMEOUT_MS }
    );
  } catch (err) {
    if (
      err instanceof BridgeError &&
      err.kind === "error" &&
      CANCEL_MESSAGE_PATTERN.test(err.message)
    ) {
      throw new CredentialCancelledError();
    }
    throw err;
  }
}
