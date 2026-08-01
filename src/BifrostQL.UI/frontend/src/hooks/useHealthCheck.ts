import { useEffect, useRef, useState } from 'react';
import type { ConnectionState } from '../connection';

/** Shape of the fields `/api/health` reports that this hook acts on. */
interface HealthResponse {
  connected?: boolean;
}

/**
 * Periodic backend health check — detects backend restarts and auto-recovers.
 *
 * Tracked with a local fail counter (rather than effect dependencies) so a 10s
 * blip doesn't tear down/recreate the interval, and recovery never remounts the
 * editor — the GraphQL client retries in place once the backend is reachable
 * again. Two consecutive failures surface the error banner; the first success
 * afterwards clears it.
 *
 * A reachable backend is not necessarily a *usable* one: the database binding
 * lives in the server's in-memory ConnectionState, and no endpoint accepts a
 * connection string over HTTP any more (task XGSUbdBiIzla), so a server restart
 * leaves `/api/health` answering 200 with `connected: false`. A client whose
 * session was restored from localStorage renders the editor regardless and then
 * waits forever on a transport that can never come up ("Connecting…"). Reading
 * `connected` and handing control back to the connect flow is what turns that
 * dead end into an actionable prompt.
 */
export function useHealthCheck(
  setErrorMessage: (message: string | null) => void,
  setConnectionState: (state: ConnectionState) => void,
  onServerUnbound: () => void,
): void {
  const [, setBackendDown] = useState(false);
  // Held in a ref so a new callback identity each render doesn't restart the
  // interval (and reset the fail/unbound counters with it).
  const onServerUnboundRef = useRef(onServerUnbound);
  onServerUnboundRef.current = onServerUnbound;

  useEffect(() => {
    let failCount = 0;
    let unboundCount = 0;
    const check = () => {
      fetch('/api/health')
        .then(async (r) => {
          if (!r.ok) throw new Error(`Server returned ${r.status}`);
          if (failCount > 0) {
            // Backend came back — clear the error banner. The editor is left
            // mounted; it re-fetches naturally as queries are retried.
            setBackendDown(false);
            setErrorMessage(null);
          }
          failCount = 0;

          const health = (await r.json().catch(() => null)) as HealthResponse | null;
          if (health?.connected === false) {
            // Require two consecutive unbound reports: a restored vault session
            // rebinds the server asynchronously on mount, and the first check
            // fires immediately, so acting on a single report would eject the
            // user mid-restore.
            unboundCount++;
            if (unboundCount >= 2) onServerUnboundRef.current();
          } else {
            unboundCount = 0;
          }
        })
        .catch(() => {
          failCount++;
          unboundCount = 0;
          if (failCount >= 2) {
            setBackendDown(true);
            setErrorMessage('Backend server is not reachable. Waiting for reconnect...');
            setConnectionState('error');
          }
        });
    };
    check();
    const id = setInterval(check, 5000);
    return () => clearInterval(id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
}
