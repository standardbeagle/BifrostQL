import { useState, useCallback, useEffect, useRef } from 'react';
import Editor from '@standardbeagle/edit-db';
import '@standardbeagle/edit-db/style.css';
import {
  WelcomePanel,
  ConnectionForm,
  ProviderSelect,
  QuickStart,
  ConnectionInfo,
  ConnectionState,
  Provider,
  QuickStartSchema,
  DataSize,
  saveRecentConnections,
  loadRecentConnections,
  fetchVaultServers,
  connectVaultServer,
} from './connection';
import type { VaultServer } from './connection/types';
import { saveSession, loadSession } from './connection/session';
import {
  createTransport,
  loadTransportMode,
  saveTransportMode,
  type QueryTransport,
  type TransportMode,
} from './lib/transport';
import {
  requestCredential,
  CredentialCancelledError,
  type ConnectionInfo as BridgeConnectionInfo,
} from './lib/credential-prompt';
import './connection/connection.css';
import './app.css';

// API endpoints
const API_TEST_CONNECTION = '/api/connection/test';
const API_QUICKSTART = '/api/database/create-quickstart';

type AppView = 'welcome' | 'quickstart' | 'provider-select' | 'connect' | 'editor';

/**
 * Extract non-sensitive structured metadata from an ADO.NET-style
 * connection string so it can be passed to the Photino credential
 * prompt. Password/Pwd is deliberately ignored — the user re-enters
 * the secret in the isolated child window, and it is never forwarded
 * to this module or the bridge payload.
 *
 * Each provider uses slightly different key names (Server vs Host,
 * User Id vs Username vs Uid, etc). We normalise them into the
 * ConnectionInfo shape here rather than making the caller branch.
 * Unknown keys are silently ignored — they'll fall through to defaults
 * on the server side.
 */
function parseConnectionStringForBridge(
  connectionString: string,
  provider: Provider
): { host?: string; port?: number; database?: string; username?: string; ssl?: boolean } {
  const parts = connectionString
    .split(';')
    .map((p) => p.trim())
    .filter((p) => p.length > 0)
    .map((p) => {
      const eq = p.indexOf('=');
      if (eq < 0) return ['', ''] as const;
      return [p.slice(0, eq).trim().toLowerCase(), p.slice(eq + 1).trim()] as const;
    });
  const lookup = new Map<string, string>(parts);
  const get = (...keys: string[]): string | undefined => {
    for (const k of keys) {
      const v = lookup.get(k);
      if (v !== undefined && v.length > 0) return v;
    }
    return undefined;
  };

  let host = get('server', 'host', 'data source');
  let port: number | undefined;
  if (provider === 'sqlserver' && host && host.includes(',')) {
    // SQL Server encodes "host,port" in the Server field.
    const comma = host.indexOf(',');
    const parsedPort = Number.parseInt(host.slice(comma + 1), 10);
    if (Number.isFinite(parsedPort)) port = parsedPort;
    host = host.slice(0, comma);
  }
  if (port === undefined) {
    const portStr = get('port');
    if (portStr) {
      const n = Number.parseInt(portStr, 10);
      if (Number.isFinite(n)) port = n;
    }
  }

  const database = get('database', 'initial catalog');
  const username = get('user id', 'username', 'uid', 'user');
  const sslModeRaw = get('sslmode', 'ssl mode');
  const ssl = sslModeRaw ? /require|verify|true|prefer/i.test(sslModeRaw) : undefined;

  return { host, port, database, username, ssl };
}

export default function App() {
  const restored = loadSession();
  const [_connectionState, setConnectionState] = useState<ConnectionState>(restored ? 'connected' : 'idle');
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const [currentView, setCurrentView] = useState<AppView>(restored ? 'editor' : 'welcome');
  const [recentConnections, setRecentConnections] = useState<ConnectionInfo[]>(() => loadRecentConnections());
  const [connectionInfo, setConnectionInfo] = useState<ConnectionInfo | null>(restored);
  const [selectedProvider, setSelectedProvider] = useState<Provider | null>(restored?.provider ?? null);
  const [isLaunching, setIsLaunching] = useState(false);
  const [launchProgress, setLaunchProgress] = useState('');
  const [editorKey, setEditorKey] = useState(0);
  const [vaultServers, setVaultServers] = useState<VaultServer[]>([]);

  // Load vault servers on mount
  useEffect(() => {
    fetchVaultServers().then(setVaultServers);
  }, []);

  // Restore backend connection if we have a saved session (survives page reloads).
  //
  // Only vault-backed sessions can be restored automatically: the raw-
  // connection-string restore path used to POST the (potentially password-
  // bearing) string to /api/connection/set, but that endpoint was deleted
  // with task XGSUbdBiIzla so no password ever crosses HTTP. Non-vault
  // sessions still render their editor view from localStorage, but the
  // server-side schema cache will rebind on the next explicit connect via
  // the credential prompt flow. Any initial command-line connection string
  // (bifrostui "<connection>") is bound at startup by Program.cs, so the
  // common dev-launch case still works without this effect.
  useEffect(() => {
    if (!restored) return;
    if (!restored.vaultServerName) return;
    fetch('/api/vault/connect', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name: restored.vaultServerName }),
    }).catch(() => {
      // Backend may not be ready yet — health check will handle recovery
    });
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  // GraphQL transport selection (HTTP/JSON vs WebSocket binary).
  // Persisted in localStorage under `bifrost-ui:transport` and toggled from
  // the editor header. The transport instance itself is held in a ref so
  // its lifecycle is independent of React renders — see the effect below.
  const [transportMode, setTransportMode] = useState<TransportMode>(() => loadTransportMode());
  const [transportConnected, setTransportConnected] = useState<boolean>(false);
  const transportRef = useRef<QueryTransport | null>(null);

  // Build (and tear down) the active transport whenever the mode flips. The
  // binary transport opens its WebSocket lazily on first query, so we issue
  // a tiny no-op probe to actually exercise the connection and drive the
  // health indicator. The probe failure surfaces as a red badge but never
  // blocks the UI — HTTP mode keeps working regardless.
  useEffect(() => {
    const transport = createTransport(transportMode, { endpoint: window.location.origin });
    transportRef.current = transport;
    setTransportConnected(transport.connected);

    let cancelled = false;
    if (transportMode === 'binary') {
      // Probe with a trivial introspection query so we can show a real
      // connected/disconnected state without waiting for the editor to
      // issue its first query (the editor doesn't yet route through this
      // transport — see TODO at the <Editor> render site).
      transport
        .query('{ __typename }')
        .then(() => {
          if (!cancelled) setTransportConnected(transport.connected);
        })
        .catch(() => {
          if (!cancelled) setTransportConnected(false);
        });
    }

    return () => {
      cancelled = true;
      transport.close();
      if (transportRef.current === transport) {
        transportRef.current = null;
      }
    };
  }, [transportMode]);

  const handleToggleTransport = useCallback(() => {
    setTransportMode((prev) => {
      const next: TransportMode = prev === 'http' ? 'binary' : 'http';
      saveTransportMode(next);
      return next;
    });
  }, []);

  // Periodic health check — detects backend restarts and auto-recovers
  const [_backendDown, setBackendDown] = useState(false);
  useEffect(() => {
    let failCount = 0;
    const check = () => {
      fetch('/api/health')
        .then((r) => {
          if (!r.ok) throw new Error(`Server returned ${r.status}`);
          if (failCount > 0) {
            // Backend came back — reload schema if in editor view
            setBackendDown(false);
            setErrorMessage(null);
            if (currentView === 'editor') setEditorKey((k) => k + 1);
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
  }, [currentView]);

  const graphqlUri = `${window.location.origin}/graphql`;

  const handleTestConnection = useCallback(async (connectionString: string): Promise<boolean> => {
    try {
      setConnectionState('testing');
      setErrorMessage(null);
      const response = await fetch(API_TEST_CONNECTION, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ connectionString }),
      });
      const result = await response.json();
      if (!response.ok) {
        throw new Error(result.error || 'Connection test failed');
      }
      return result.success;
    } catch (err) {
      const msg = err instanceof Error ? err.message : 'Connection test failed';
      setErrorMessage(msg.includes('Failed to fetch')
        ? 'Cannot reach the backend server. It may have crashed or failed to start.'
        : msg);
      return false;
    } finally {
      setConnectionState('idle');
    }
  }, []);

  const handleConnect = useCallback(async (connectionString: string, connectionName: string, provider?: Provider) => {
    const resolvedProvider = provider ?? selectedProvider ?? 'sqlserver';
    try {
      setConnectionState('connecting');
      setErrorMessage(null);

      const response = await fetch(API_TEST_CONNECTION, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ connectionString }),
      });

      const result = await response.json();
      if (!response.ok) {
        throw new Error(result.error || 'Connection failed');
      }

      // Route activation through the native bridge credential prompt
      // and the vault flow. The legacy /api/connection/set endpoint
      // (which accepted the raw connection string including password
      // over HTTP) has been deleted as part of task XGSUbdBiIzla.
      //
      // We parse the connection string locally for the non-secret
      // metadata (host/port/database/username) and pass those to the
      // bridge as a ConnectionInfo — the password is deliberately
      // dropped on the floor, and the user re-enters it in the
      // isolated Photino child window. That window persists the full
      // VaultServer entry server-side, and we then activate it via
      // /api/vault/connect. No password ever touches HTTP traffic
      // and the main webview heap holds it only for the brief window
      // between ConnectionForm submit and this callback running.
      const parsed = parseConnectionStringForBridge(connectionString, resolvedProvider);
      const bridgeInfo: BridgeConnectionInfo = {
        vaultName: connectionName,
        provider: resolvedProvider,
        ...parsed,
      };
      const promptResult = await requestCredential(bridgeInfo);

      // Refetch vault servers so the UI's server list reflects the
      // newly-persisted entry, then activate via the vault connect path.
      setVaultServers(await fetchVaultServers());

      const connectResult = await connectVaultServer(promptResult.name);
      if (!connectResult.success) {
        throw new Error(connectResult.error || 'Failed to activate vault entry');
      }

      const info: ConnectionInfo = {
        id: Date.now().toString(),
        name: connectionName,
        connectionString: '', // never persist the raw string on bridge flow
        connectedAt: new Date().toISOString(),
        server: connectResult.server ?? connectionName,
        database: connectResult.database ?? connectionName,
        provider: (connectResult.provider as Provider) ?? resolvedProvider,
        vaultServerName: promptResult.name,
      };

      setConnectionInfo(info);
      saveSession(info);

      setEditorKey((k) => k + 1);
      setCurrentView('editor');
      setConnectionState('connected');
    } catch (err) {
      if (err instanceof CredentialCancelledError) {
        // User dismissed the credential prompt — return to idle without
        // surfacing an error. The inline form state is still intact so
        // they can retry or tweak the metadata.
        setConnectionState('idle');
        return;
      }
      setConnectionState('error');
      const msg = err instanceof Error ? err.message : 'Connection failed';
      setErrorMessage(msg.includes('Failed to fetch')
        ? 'Cannot reach the backend server. It may have crashed or failed to start.'
        : msg);
    }
  }, [selectedProvider]);

  const handleSelectRecentConnection = useCallback((connection: ConnectionInfo) => {
    const provider = connection.provider ?? 'sqlserver';
    setSelectedProvider(provider);
    handleConnect(connection.connectionString, connection.name, provider);
  }, [handleConnect]);

  const handleConnectVaultServer = useCallback(async (name: string) => {
    try {
      setConnectionState('connecting');
      setErrorMessage(null);
      const result = await connectVaultServer(name);
      if (!result.success) {
        throw new Error(result.error || 'Failed to connect');
      }

      const info: ConnectionInfo = {
        id: Date.now().toString(),
        name: result.name ?? name,
        connectionString: '', // Vault connections don't expose the connection string
        connectedAt: new Date().toISOString(),
        server: result.server ?? '',
        database: result.database ?? '',
        provider: (result.provider as Provider) ?? 'postgres',
        vaultServerName: name,
      };

      setConnectionInfo(info);
      saveSession(info);
      setEditorKey((k) => k + 1);
      setCurrentView('editor');
      setConnectionState('connected');
    } catch (err) {
      setConnectionState('error');
      const msg = err instanceof Error ? err.message : 'Connection failed';
      setErrorMessage(msg.includes('Failed to fetch')
        ? 'Cannot reach the backend server.'
        : msg);
    }
  }, []);

  const handleTryItNow = useCallback(() => {
    setCurrentView('quickstart');
  }, []);

  const handleProviderSelect = useCallback((provider: Provider) => {
    setSelectedProvider(provider);
    setCurrentView('connect');
  }, []);

  const handleQuickStartLaunch = useCallback(async (schema: QuickStartSchema, dataSize: DataSize) => {
    try {
      setConnectionState('connecting');
      setErrorMessage(null);
      setIsLaunching(true);
      setLaunchProgress('Starting...');

      const response = await fetch(API_QUICKSTART, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'Accept': 'text/event-stream' },
        body: JSON.stringify({ schema, dataSize }),
      });

      if (!response.ok) {
        const result = await response.json();
        throw new Error(result.error || 'Failed to create quickstart database');
      }

      const contentType = response.headers.get('content-type') ?? '';
      if (contentType.includes('text/event-stream') && response.body) {
        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';
        let connectionString = '';

        for (;;) {
          const { done, value } = await reader.read();
          if (done) break;
          buffer += decoder.decode(value, { stream: true });

          const lines = buffer.split('\n');
          buffer = lines.pop() ?? '';

          for (const line of lines) {
            if (!line.startsWith('data: ')) continue;
            try {
              const event = JSON.parse(line.slice(6));
              if (event.message) setLaunchProgress(event.message);
              if (event.connectionString) connectionString = event.connectionString;
            } catch {
              // skip malformed SSE data lines
            }
          }
        }

        if (connectionString) {
          // No follow-up POST needed — /api/database/create-quickstart
          // now self-binds the freshly created SQLite database on the
          // server side before emitting the Complete! event, so by the
          // time we get here BifrostQL is already pointed at the new
          // file. SQLite connection strings carry no credentials so
          // this side-band bind does not re-introduce the password-
          // over-HTTP issue that prompted deleting /api/connection/set.
          const info: ConnectionInfo = {
            id: Date.now().toString(),
            name: `QuickStart - ${schema}`,
            connectionString,
            connectedAt: new Date().toISOString(),
            server: 'localhost',
            database: schema,
            provider: 'sqlite',
          };

          setConnectionInfo(info);
          saveSession(info);
          const updated = [...recentConnections.filter((c) => c.connectionString !== connectionString), info];
          setRecentConnections(updated.slice(0, 5));
          saveRecentConnections(updated.slice(0, 5));

          setEditorKey((k) => k + 1);
          setCurrentView('editor');
          setConnectionState('connected');
        } else {
          throw new Error('No connection string received from server');
        }
      } else {
        const result = await response.json();

        // Non-SSE path — the server has already self-bound to the new
        // SQLite database before returning. See the SSE branch above
        // and the self-bind block in /api/database/create-quickstart
        // (Program.cs) for the rationale.
        const info: ConnectionInfo = {
          id: Date.now().toString(),
          name: `QuickStart - ${schema}`,
          connectionString: result.connectionString,
          connectedAt: new Date().toISOString(),
          server: 'localhost',
          database: schema,
          provider: 'sqlite',
        };

        setConnectionInfo(info);
        saveSession(info);
        const updated = [...recentConnections.filter((c) => c.connectionString !== result.connectionString), info];
        setRecentConnections(updated.slice(0, 5));
        saveRecentConnections(updated.slice(0, 5));

        setEditorKey((k) => k + 1);
        setCurrentView('editor');
        setConnectionState('connected');
      }
    } catch (err) {
      setConnectionState('error');
      const msg = err instanceof Error ? err.message : 'Failed to create quickstart database';
      setErrorMessage(msg.includes('Failed to fetch')
        ? 'Cannot reach the backend server. It may have crashed or failed to start.'
        : msg);
    } finally {
      setIsLaunching(false);
      setLaunchProgress('');
    }
  }, [recentConnections]);

  const handleBack = useCallback(() => {
    setErrorMessage(null);
    switch (currentView) {
      case 'quickstart':
      case 'provider-select':
        setCurrentView('welcome');
        break;
      case 'connect':
        setCurrentView('provider-select');
        setSelectedProvider(null);
        break;
      case 'editor':
        fetch('/api/ssh/disconnect', { method: 'POST' }).catch(() => {});
        setCurrentView('welcome');
        setConnectionInfo(null);
        saveSession(null);
        setConnectionState('idle');
        setErrorMessage(null);
        setSelectedProvider(null);
        break;
      default:
        setCurrentView('welcome');
    }
  }, [currentView]);

  const errorBanner = errorMessage && (
    <div className="bifrost-error-banner" role="alert">
      <span className="bifrost-error-banner__icon">!</span>
      <span className="bifrost-error-banner__message">{errorMessage}</span>
      <button
        className="bifrost-error-banner__dismiss"
        onClick={() => { setErrorMessage(null); setConnectionState('idle'); }}
        aria-label="Dismiss error"
      >
        &times;
      </button>
    </div>
  );

  if (currentView === 'quickstart') {
    return (
      <div className="bifrost-connection-container">
        {errorBanner}
        <QuickStart
          onLaunch={handleQuickStartLaunch}
          onBack={handleBack}
          isLaunching={isLaunching}
          launchProgress={launchProgress}
        />
      </div>
    );
  }

  if (currentView === 'provider-select') {
    return (
      <div className="bifrost-connection-container">
        {errorBanner}
        <ProviderSelect
          onProviderSelect={handleProviderSelect}
          onBack={handleBack}
        />
      </div>
    );
  }

  if (currentView === 'connect' && selectedProvider) {
    return (
      <div className="bifrost-connection-container">
        {errorBanner}
        <ConnectionForm
          provider={selectedProvider}
          onConnect={handleConnect}
          onTestConnection={handleTestConnection}
          onBack={handleBack}
        />
      </div>
    );
  }

  if (currentView === 'editor') {
    return (
      <div className="app-container">
        <div className="bifrost-header">
          <div className="bifrost-header__brand">
            <div className="bifrost-header__logo">B</div>
            <h1>BifrostQL</h1>
          </div>
          {connectionInfo && <>
            <div className="bifrost-header__separator" />
            <span className="bifrost-database-info">{connectionInfo.name}</span>
          </>}
          <div className="bifrost-header__spacer" />
          <div
            className="bifrost-transport-toggle"
            role="group"
            aria-label="GraphQL transport"
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: 8,
              marginRight: 12,
              fontSize: 12,
            }}
          >
            <span
              aria-label={
                transportMode === 'binary'
                  ? transportConnected
                    ? 'Binary transport connected'
                    : 'Binary transport disconnected'
                  : 'HTTP transport (stateless)'
              }
              title={
                transportMode === 'binary'
                  ? transportConnected
                    ? 'Binary WebSocket connected'
                    : 'Binary WebSocket disconnected'
                  : 'HTTP transport — stateless, always reachable'
              }
              style={{
                width: 8,
                height: 8,
                borderRadius: '50%',
                background:
                  transportMode === 'http'
                    ? '#9ca3af'
                    : transportConnected
                      ? '#22c55e'
                      : '#ef4444',
                display: 'inline-block',
              }}
            />
            <button
              type="button"
              onClick={handleToggleTransport}
              aria-pressed={transportMode === 'binary'}
              title="Toggle between HTTP/JSON and WebSocket binary transport"
              style={{
                background: 'transparent',
                border: '1px solid currentColor',
                borderRadius: 4,
                padding: '2px 8px',
                cursor: 'pointer',
                font: 'inherit',
                color: 'inherit',
              }}
            >
              {transportMode === 'binary' ? 'Binary' : 'HTTP'}
            </button>
          </div>
          <button
            className="bifrost-disconnect-button"
            onClick={handleBack}
          >
            Disconnect
          </button>
        </div>
        {/*
          TODO(transport-integration): The Editor from @standardbeagle/edit-db
          owns its own GraphQL client built from `uri` and does not yet accept
          a pluggable QueryTransport instance. Once the Editor exposes a
          transport hook (or accepts a `transport` prop), pass
          `transportRef.current` here so editor queries actually route through
          the selected mode. Until then the header toggle exercises the
          binary client via a probe and the editor stays on HTTP regardless of
          the toggle. Tracked alongside the binary transport rollout work.
        */}
        <Editor
          key={editorKey}
          uri={graphqlUri}
          onLocate={(location) => {
            window.history.pushState(null, '', location);
          }}
        />
      </div>
    );
  }

  // Welcome view (default)
  return (
    <div className="bifrost-welcome-container">
      {errorBanner}
      <WelcomePanel
        onConnectClick={() => { setErrorMessage(null); setCurrentView('provider-select'); }}
        onCreateTestDatabase={handleTryItNow}
        recentConnections={recentConnections}
        onSelectRecentConnection={handleSelectRecentConnection}
        onClearRecentConnections={() => {
          setRecentConnections([]);
          saveRecentConnections([]);
        }}
        vaultServers={vaultServers}
        onConnectVaultServer={handleConnectVaultServer}
      />
    </div>
  );
}
