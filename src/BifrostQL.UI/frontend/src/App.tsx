import { useState, useCallback, useEffect, useMemo, useRef } from 'react';
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
  type ConnectionRequest,
} from './connection';
import type { VaultServer } from './connection/types';
import { AboutPanel } from './about/AboutPanel';
import { saveSession, loadSession } from './connection/session';
import {
  createTransport,
  loadTransportMode,
  saveTransportMode,
  type QueryTransport,
  type TransportMode,
} from './lib/transport';
import { TransportGraphQLFetcher } from './lib/transport-fetcher';
import {
  requestCredential,
  saveVaultEntry,
  CredentialCancelledError,
  type ConnectionInfo as BridgeConnectionInfo,
} from './lib/credential-prompt';
import { SqlConsole } from './SqlConsole';
import { QueryBuilderPane } from './designer/QueryBuilderPane';
import { FormBuilderPane } from './forms/FormBuilderPane';
import { isSqlBridgeAvailable } from './lib/sql-bridge';
import { ProfileDropdown } from './profiles/ProfileDropdown';
import {
  fetchProfiles,
  resolveActiveProfile,
  saveActiveProfileId,
  DEFAULT_PROFILES,
} from './profiles/profiles';
import type { ApiProfile } from './profiles/types';
import './connection/connection.css';
import './app.css';

// API endpoints
const API_QUICKSTART = '/api/database/create-quickstart';

type AppView = 'welcome' | 'quickstart' | 'provider-select' | 'connect' | 'editor' | 'about';

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

function requestToBridgeInfo(request: ConnectionRequest): BridgeConnectionInfo {
  return {
    vaultName: request.name,
    provider: request.provider,
    host: request.host,
    port: request.port,
    database: request.database,
    username: request.username,
    ssl: request.ssl,
    ssh: request.ssh?.enabled ? {
      host: request.ssh.sshHost,
      port: request.ssh.sshPort,
      username: request.ssh.sshUsername,
      identityFile: request.ssh.identityFile || undefined,
    } : undefined,
    tags: request.tags,
  };
}

export default function App() {
  const restored = loadSession();
  const [_connectionState, setConnectionState] = useState<ConnectionState>(restored ? 'connected' : 'idle');
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const [currentView, setCurrentView] = useState<AppView>(restored ? 'editor' : 'welcome');
  // View to return to when leaving the About page (opened from welcome or editor).
  const [aboutReturnView, setAboutReturnView] = useState<AppView>('welcome');
  const [recentConnections, setRecentConnections] = useState<ConnectionInfo[]>(() => loadRecentConnections());
  const [connectionInfo, setConnectionInfo] = useState<ConnectionInfo | null>(restored);
  const [selectedProvider, setSelectedProvider] = useState<Provider | null>(restored?.provider ?? null);
  const [isLaunching, setIsLaunching] = useState(false);
  const [launchProgress, setLaunchProgress] = useState('');
  const [editorKey, setEditorKey] = useState(0);
  // API profiles (slice 6a endpoint). The picker re-points the embedded editor
  // at `?profile=<serverProfile>` so the server serves that profile's schema.
  const [apiProfiles, setApiProfiles] = useState<ApiProfile[]>(DEFAULT_PROFILES);
  const [activeProfileId, setActiveProfileId] = useState<string>(
    () => resolveActiveProfile(DEFAULT_PROFILES).id,
  );
  // Editor pane toggle: GraphQL editor (default) vs raw SQL console. The SQL
  // console rides the Photino bridge, so it's only offered inside the desktop app.
  const [editorPane, setEditorPane] = useState<'graphql' | 'sql' | 'builder' | 'forms'>('graphql');
  const sqlBridgeAvailable = isSqlBridgeAvailable();
  const [vaultServers, setVaultServers] = useState<VaultServer[]>([]);

  // Load vault servers on mount
  useEffect(() => {
    fetchVaultServers().then(({ servers, error }) => {
      setVaultServers(servers);
      if (error) setErrorMessage(error);
    });
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
  // the editor header. The active transport instance is derived from this
  // mode (and the active profile) further down, once `activeProfile` is
  // resolved, and injected into the embedded editor as its GraphQL fetcher.
  const [transportMode, setTransportMode] = useState<TransportMode>(() => loadTransportMode());
  const [transportConnected, setTransportConnected] = useState<boolean>(false);

  const handleToggleTransport = useCallback(() => {
    setTransportMode((prev) => {
      const next: TransportMode = prev === 'http' ? 'binary' : 'http';
      saveTransportMode(next);
      return next;
    });
  }, []);

  // Periodic health check — detects backend restarts and auto-recovers.
  // Tracked in a ref (rather than an effect dependency) so a 10s blip
  // doesn't tear down/recreate the interval on every currentView change,
  // and recovery never remounts the editor (setEditorKey) — the GraphQL
  // client itself retries in place once the backend is reachable again.
  // Remounting here used to force a full re-introspect + refetch on any
  // transient hiccup; the profile-resolution effect above already handles
  // the one case (profile actually changed) where a remount is warranted.
  const [_backendDown, setBackendDown] = useState(false);
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
  }, []);

  // Refresh the profile list whenever the active connection changes. The
  // server schema is connection-scoped, so each connection may expose a
  // different set of module profiles. On failure fetchProfiles() falls back to
  // DEFAULT_PROFILES (single raw entry → picker disabled).
  const connectionKey = connectionInfo?.id ?? null;
  // Tracks the last resolved profile id so the editor only remounts on a real
  // change (kept in a ref so the comparison stays out of a setState updater).
  const resolvedProfileRef = useRef(activeProfileId);
  useEffect(() => {
    let cancelled = false;
    fetchProfiles().then((fetched) => {
      if (cancelled) return;
      setApiProfiles(fetched);
      const nextId = resolveActiveProfile(fetched).id;
      // Only remount the editor when the resolved profile actually changed.
      // Bumping unconditionally made the editor mount twice on every startup
      // (once on mount, again when profiles resolved to the same default),
      // re-introspecting the schema and refetching all table data for nothing.
      if (resolvedProfileRef.current !== nextId) {
        resolvedProfileRef.current = nextId;
        setEditorKey((k) => k + 1);
      }
      setActiveProfileId(nextId);
    });
    return () => { cancelled = true; };
  }, [connectionKey]);

  const handleSelectProfile = useCallback((id: string) => {
    saveActiveProfileId(id);
    setActiveProfileId(id);
    resolvedProfileRef.current = id;
    // Remount the editor so it re-introspects the newly selected profile's
    // schema from the profile-scoped GraphQL endpoint.
    setEditorKey((k) => k + 1);
  }, []);

  const activeProfile = apiProfiles.find((p) => p.id === activeProfileId) ?? apiProfiles[0];
  // The server serves each module profile's schema behind a `?profile=` query
  // param. We thread that onto both the HTTP GraphQL path and the binary
  // WebSocket path so the selected transport hits the right profile-scoped
  // endpoint.
  const serverProfile = activeProfile.serverProfile;
  const graphqlPath = serverProfile
    ? `/graphql?profile=${encodeURIComponent(serverProfile)}`
    : '/graphql';
  const binaryPath = serverProfile
    ? `/bifrost-ws?profile=${encodeURIComponent(serverProfile)}`
    : '/bifrost-ws';

  // The active transport is owned entirely by the effect below: it is created,
  // health-probed, and torn down there, then published to render via state.
  // Keeping creation out of render (useMemo) is what makes this StrictMode-safe
  // — a memoized instance closed by the effect's cleanup would be reused on the
  // remount and every binary query would throw "BinaryTransport is closed". The
  // effect instead builds a fresh instance on each run, so the mount → cleanup →
  // remount cycle always ends on a live transport.
  const [transport, setTransport] = useState<QueryTransport | null>(null);

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
    setTransport(active);
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
  }, [transportMode, graphqlPath, binaryPath]);

  // Adapter that lets the embedded editor route every GraphQL request through
  // the selected transport. Rebuilt alongside the transport so the editor
  // remount (keyed on transportMode below) picks up the new routing. Null until
  // the effect has published the first transport instance.
  const editorFetcher = useMemo(
    () => (transport ? new TransportGraphQLFetcher(transport) : null),
    [transport],
  );

  const activateSavedVaultEntry = useCallback(async (
    vaultName: string,
    displayName: string,
    fallbackProvider: Provider,
  ): Promise<ConnectionInfo> => {
    const { servers, error: listError } = await fetchVaultServers();
    setVaultServers(servers);
    if (listError) setErrorMessage(listError);

    const connectResult = await connectVaultServer(vaultName);
    if (!connectResult.success) {
      throw new Error(connectResult.error || 'Failed to activate vault entry');
    }

    return {
      id: Date.now().toString(),
      name: displayName,
      connectionString: '',
      connectedAt: new Date().toISOString(),
      server: connectResult.server ?? displayName,
      database: connectResult.database ?? displayName,
      provider: (connectResult.provider as Provider) ?? fallbackProvider,
      vaultServerName: vaultName,
    };
  }, []);

  const handleTestConnection = useCallback(async (request: ConnectionRequest): Promise<boolean> => {
    try {
      setConnectionState('testing');
      setErrorMessage(null);

      if (request.provider === 'sqlite' && request.connectionString) {
        const response = await fetch('/api/connection/test', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ connectionString: request.connectionString }),
        });
        const result = await response.json();
        if (!response.ok) {
          throw new Error(result.error || 'Connection test failed');
        }
        return result.success;
      }

      setErrorMessage('Use Connect to save credentials and validate this server.');
      return false;
    } catch (err) {
      if (err instanceof CredentialCancelledError) return false;
      const msg = err instanceof Error ? err.message : 'Connection test failed';
      setErrorMessage(msg.includes('Failed to fetch')
        ? 'Cannot reach the backend server. It may have crashed or failed to start.'
        : msg);
      return false;
    } finally {
      setConnectionState('idle');
    }
  }, [activateSavedVaultEntry]);

  const handleConnect = useCallback(async (request: ConnectionRequest) => {
    const resolvedProvider = request.provider ?? selectedProvider ?? 'sqlserver';
    try {
      setConnectionState('connecting');
      setErrorMessage(null);

      if (resolvedProvider === 'sqlite' && request.connectionString) {
        const response = await fetch('/api/connection/test', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ connectionString: request.connectionString }),
        });
        const result = await response.json();
        if (!response.ok) {
          throw new Error(result.error || 'Connection failed');
        }

        const info: ConnectionInfo = {
          id: Date.now().toString(),
          name: request.name,
          connectionString: request.connectionString,
          connectedAt: new Date().toISOString(),
          server: 'localhost',
          database: request.database ?? request.name,
          provider: resolvedProvider,
        };

        setConnectionInfo(info);
        saveSession(info);
        setEditorKey((k) => k + 1);
        setCurrentView('editor');
        setConnectionState('connected');
        return;
      }

      // Route activation through the native bridge credential prompt
      // and the vault flow. The legacy /api/connection/set endpoint
      // (which accepted the raw connection string including password
      // over HTTP) has been deleted as part of task XGSUbdBiIzla.
      //
      // We pass only non-secret metadata to the bridge. If the user chose raw
      // connection-string mode, parse and discard any password-like fields here;
      // the credential prompt remains the only place where DB passwords are
      // collected.
      const bridgeInfo = request.connectionString
        ? {
          vaultName: request.name,
          provider: resolvedProvider,
          ...parseConnectionStringForBridge(request.connectionString, resolvedProvider),
        }
        : requestToBridgeInfo(request);
      const promptResult = request.requiresCredential === false
        ? await saveVaultEntry(bridgeInfo)
        : await requestCredential(bridgeInfo);
      const info = await activateSavedVaultEntry(promptResult.name, request.name, resolvedProvider);

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
  }, [activateSavedVaultEntry, selectedProvider]);

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

  const handleSelectRecentConnection = useCallback((connection: ConnectionInfo) => {
    const provider = connection.provider ?? 'sqlserver';
    setSelectedProvider(provider);
    if (connection.vaultServerName) {
      handleConnectVaultServer(connection.vaultServerName);
      return;
    }
    handleConnect({
      name: connection.name,
      provider,
      connectionString: connection.connectionString,
      database: connection.database,
    });
  }, [handleConnect, handleConnectVaultServer]);

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

        const processLine = (line: string) => {
          if (!line.startsWith('data: ')) return;
          try {
            const event = JSON.parse(line.slice(6));
            if (event.message) setLaunchProgress(event.message);
            if (event.connectionString) connectionString = event.connectionString;
          } catch {
            // skip malformed SSE data lines
          }
        };

        for (;;) {
          const { done, value } = await reader.read();
          if (done) break;
          buffer += decoder.decode(value, { stream: true });

          const lines = buffer.split('\n');
          buffer = lines.pop() ?? '';
          for (const line of lines) processLine(line);
        }

        // Flush the trailing line: the final `Complete!` event (which carries the
        // connection string) often arrives without a trailing newline, so it sits
        // in the buffer after the loop ends and would otherwise be dropped.
        buffer += decoder.decode();
        if (buffer.length > 0) processLine(buffer);

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

  const handleShowAbout = useCallback(() => {
    setAboutReturnView(currentView);
    setCurrentView('about');
  }, [currentView]);

  const handleBack = useCallback(() => {
    setErrorMessage(null);
    switch (currentView) {
      case 'about':
        setCurrentView(aboutReturnView);
        return;
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
  }, [currentView, aboutReturnView]);

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

  if (currentView === 'about') {
    return (
      <div className="bifrost-connection-container">
        {errorBanner}
        <AboutPanel onBack={handleBack} />
      </div>
    );
  }

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
          <ProfileDropdown
            profiles={apiProfiles}
            activeId={activeProfileId}
            onSelect={handleSelectProfile}
          />
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
                  : 'HTTP transport'
              }
              title={
                transportMode === 'binary'
                  ? transportConnected
                    ? 'Editor queries route over the binary WebSocket transport (connected)'
                    : 'Binary WebSocket transport disconnected; reconnecting'
                  : 'Editor queries route over the HTTP/JSON transport'
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
              title="Route editor queries over the HTTP/JSON or WebSocket binary transport."
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
              {transportMode === 'binary' ? 'Binary' : 'GraphQL'}
            </button>
          </div>
          {sqlBridgeAvailable && (
            <div role="group" aria-label="Editor pane" style={{ display: 'flex', gap: 4, marginRight: 12 }}>
              {([
                ['graphql', 'GraphQL'],
                ['sql', 'SQL'],
                ['builder', 'Query builder'],
                ['forms', 'Form builder'],
              ] as const).map(([pane, label]) => (
                <button
                  key={pane}
                  type="button"
                  onClick={() => setEditorPane(pane)}
                  aria-pressed={editorPane === pane}
                  style={{
                    background: editorPane === pane ? '#2563eb' : 'transparent',
                    color: editorPane === pane ? '#fff' : 'inherit',
                    border: '1px solid currentColor',
                    borderRadius: 4,
                    padding: '2px 8px',
                    cursor: 'pointer',
                    font: 'inherit',
                    fontSize: 12,
                  }}
                >
                  {label}
                </button>
              ))}
            </div>
          )}
          <button
            className="bifrost-disconnect-button"
            onClick={handleShowAbout}
            style={{ marginRight: 8 }}
          >
            About
          </button>
          <button
            className="bifrost-disconnect-button"
            onClick={handleBack}
          >
            Disconnect
          </button>
        </div>
        {/*
          The Editor from @standardbeagle/edit-db accepts a `fetcher` prop
          implementing its GraphQLFetcher interface. We inject a
          TransportGraphQLFetcher that delegates to the selected QueryTransport,
          so every editor data path (schema introspection, table data,
          mutations, stats) routes through the active HTTP/JSON or binary
          WebSocket transport. Toggling the header control rebuilds the
          transport and remounts the editor (via the transportMode key) so new
          queries flow over the newly selected mode.
        */}
        {editorPane === 'sql' ? (
          <SqlConsole />
        ) : editorPane === 'builder' ? (
          <QueryBuilderPane />
        ) : editorPane === 'forms' ? (
          <FormBuilderPane />
        ) : editorFetcher && transport && transport.mode === transportMode ? (
          // Only mount the editor once the effect has published a transport
          // whose mode matches the current selection. During a mode toggle the
          // old (now-closed) transport is still in state for one render; gating
          // on the mode match keeps the editor off the closed instance until the
          // fresh one is ready, avoiding a "BinaryTransport is closed" query.
          <Editor
            key={`${editorKey}-${transportMode}`}
            fetcher={editorFetcher}
            showStats
            onLocate={(location) => {
              window.history.pushState(null, '', location);
            }}
          />
        ) : (
          <div className="bifrost-editor-loading" style={{ padding: 24, opacity: 0.7 }}>
            Connecting…
          </div>
        )}
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
        onShowAbout={handleShowAbout}
      />
    </div>
  );
}
