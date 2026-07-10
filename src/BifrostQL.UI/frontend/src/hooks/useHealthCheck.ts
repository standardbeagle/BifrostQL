import { useEffect, useState } from 'react';
import type { ConnectionState } from '../connection';

/**
 * Periodic backend health check — detects backend restarts and auto-recovers.
 *
 * Tracked with a local fail counter (rather than effect dependencies) so a 10s
 * blip doesn't tear down/recreate the interval, and recovery never remounts the
 * editor — the GraphQL client retries in place once the backend is reachable
 * again. Two consecutive failures surface the error banner; the first success
 * afterwards clears it.
 */
export function useHealthCheck(
  setErrorMessage: (message: string | null) => void,
  setConnectionState: (state: ConnectionState) => void,
): void {
  const [, setBackendDown] = useState(false);
  useEffect(() => {
    let failCount = 0;
    const check = () => {
      fetch('/api/health')
        .then((r) => {
          if (!r.ok) throw new Error(`Server returned ${r.status}`);
          if (failCount > 0) {
            // Backend came back — clear the error banner. The editor is left
            // mounted; it re-fetches naturally as queries are retried.
            setBackendDown(false);
            setErrorMessage(null);
          }
          failCount = 0;
        })
        .catch(() => {
          failCount++;
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
