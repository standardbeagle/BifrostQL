import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  createTransport,
  loadTransportMode,
  saveTransportMode,
  type QueryTransport,
  type TransportMode,
} from '../lib/transport';
import { TransportGraphQLFetcher } from '../lib/transport-fetcher';

export interface UseTransportResult {
  transportMode: TransportMode;
  toggleTransport: () => void;
  transportConnected: boolean;
  /**
   * Fetcher for the CURRENTLY selected mode and profile paths, or null while
   * the effect has yet to build one for them. Never a fetcher over a superseded
   * transport: a consumer that mounts children on this value therefore cannot
   * issue a request to the previous profile's endpoint.
   */
  editorFetcher: TransportGraphQLFetcher | null;
}

/**
 * Owns the GraphQL transport selection (HTTP/JSON vs WebSocket binary) and the
 * live transport instance the embedded editor routes through.
 *
 * The transport is created, health-probed, and torn down inside the effect, then
 * published to render via state. Keeping creation out of render (useMemo) is what
 * makes this StrictMode-safe — a memoized instance closed by the effect's cleanup
 * would be reused on the remount and every binary query would throw
 * "BinaryTransport is closed". The effect instead builds a fresh instance on each
 * run, so the mount → cleanup → remount cycle always ends on a live transport.
 *
 * `graphqlPath`/`binaryPath` carry the active profile's `?profile=` query param so
 * the selected transport hits the right profile-scoped endpoint.
 */
export function useTransport(graphqlPath: string, binaryPath: string): UseTransportResult {
  const [transportMode, setTransportMode] = useState<TransportMode>(() => loadTransportMode());
  const [transportConnected, setTransportConnected] = useState<boolean>(false);
  // The transport is published together with the identity it was built from, so
  // render can tell a live transport from one the effect has not rebuilt yet.
  // Publishing the instance alone left a window — one render after a profile or
  // mode change — where the selection had moved but the transport had not, and
  // anything mounted in that window queried the superseded endpoint.
  const [published, setPublished] = useState<{ key: string; transport: QueryTransport } | null>(null);
  const transportKey = `${transportMode}|${graphqlPath}|${binaryPath}`;

  const toggleTransport = useCallback(() => {
    setTransportMode((prev) => {
      const next: TransportMode = prev === 'http' ? 'binary' : 'http';
      saveTransportMode(next);
      return next;
    });
  }, []);

  useEffect(() => {
    let cancelled = false;
    const active = createTransport(
      transportMode,
      { endpoint: window.location.origin, graphqlPath, binaryPath },
      undefined,
      // Live connection-state callback: the binary client fires this on open,
      // close, reconnect, and reconnect-failure, so the header badge tracks a
      // mid-session disconnect instead of freezing on its first sample.
      { onConnectedChange: (connected) => { if (!cancelled) setTransportConnected(connected); } },
    );
    setPublished({ key: transportKey, transport: active });
    setTransportConnected(active.connected);
    // The binary transport opens its WebSocket lazily on first query, so issue a
    // tiny probe to exercise the connection up front; the editor shares this same
    // instance so its queries reuse the probed socket. A probe failure surfaces
    // as a red badge but never blocks the UI.
    if (active.mode === 'binary') {
      active
        .query('{ __typename }')
        .then(() => {
          if (!cancelled) setTransportConnected(active.connected);
        })
        .catch(() => {
          if (!cancelled) setTransportConnected(false);
        });
    }
    return () => {
      cancelled = true;
      active.close();
    };
  }, [transportKey, transportMode, graphqlPath, binaryPath]);

  // Adapter that lets the embedded editor route every GraphQL request through
  // the selected transport. Null until the effect has published a transport
  // built from the CURRENT selection — including across a change, so a consumer
  // gated on this value unmounts rather than firing a query at the endpoint the
  // user just navigated away from.
  const editorFetcher = useMemo(
    () => (published && published.key === transportKey
      ? new TransportGraphQLFetcher(published.transport)
      : null),
    [published, transportKey],
  );

  return { transportMode, toggleTransport, transportConnected, editorFetcher };
}
