import { useCallback, useEffect, useState } from 'react';
import {
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
} from '../connection';
import type { VaultServer } from '../connection/types';
import { saveSession } from '../connection/session';
import { parseAdoConnectionString } from '../connection/sanitize-connection';
import {
  requestCredential,
  saveVaultEntry,
  CredentialCancelledError,
  type ConnectionInfo as BridgeConnectionInfo,
} from '../lib/credential-prompt';
import { toUserFacingError } from '../lib/user-error';
import { parseQuickstartStream } from '../lib/quickstart-stream';

// API endpoints
const API_QUICKSTART = '/api/database/create-quickstart';

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

export interface UseConnectionFlowsParams {
  /** Session restored from localStorage on mount (null when starting fresh). */
  restored: ConnectionInfo | null;
  /** Transition into the editor view (bumps the editor key + sets the view). */
  enterEditor: () => void;
}

export interface UseConnectionFlowsResult {
  connectionState: ConnectionState;
  setConnectionState: (state: ConnectionState) => void;
  errorMessage: string | null;
  setErrorMessage: (message: string | null) => void;
  connectionInfo: ConnectionInfo | null;
  recentConnections: ConnectionInfo[];
  vaultServers: VaultServer[];
  selectedProvider: Provider | null;
  setSelectedProvider: (provider: Provider | null) => void;
  isLaunching: boolean;
  launchProgress: string;
  handleTestConnection: (request: ConnectionRequest) => Promise<boolean>;
  handleConnect: (request: ConnectionRequest) => Promise<void>;
  handleConnectVaultServer: (name: string) => Promise<void>;
  handleSelectRecentConnection: (connection: ConnectionInfo) => void;
  handleQuickStartLaunch: (schema: QuickStartSchema, dataSize: DataSize) => Promise<void>;
  handleClearRecentConnections: () => void;
  handleDisconnect: () => void;
}

/**
 * Owns the connection lifecycle: testing/connecting (raw SQLite, vault-backed,
 * and native-bridge credential flows), the quickstart database launch, recent
 * connections, and vault-server discovery. On success each flow publishes a
 * ConnectionInfo and transitions to the editor via `enterEditor`.
 */
export function useConnectionFlows({ restored, enterEditor }: UseConnectionFlowsParams): UseConnectionFlowsResult {
  const [connectionState, setConnectionState] = useState<ConnectionState>(restored ? 'connected' : 'idle');
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const [connectionInfo, setConnectionInfo] = useState<ConnectionInfo | null>(restored);
  const [selectedProvider, setSelectedProvider] = useState<Provider | null>(restored?.provider ?? null);
  const [recentConnections, setRecentConnections] = useState<ConnectionInfo[]>(() => loadRecentConnections());
  const [isLaunching, setIsLaunching] = useState(false);
  const [launchProgress, setLaunchProgress] = useState('');
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
      setErrorMessage(toUserFacingError(err, 'Connection test failed'));
      return false;
    } finally {
      setConnectionState('idle');
    }
  }, []);

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
        enterEditor();
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
          ...parseAdoConnectionString(request.connectionString, resolvedProvider),
        }
        : requestToBridgeInfo(request);
      const promptResult = request.requiresCredential === false
        ? await saveVaultEntry(bridgeInfo)
        : await requestCredential(bridgeInfo);
      const info = await activateSavedVaultEntry(promptResult.name, request.name, resolvedProvider);

      setConnectionInfo(info);
      saveSession(info);

      enterEditor();
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
      setErrorMessage(toUserFacingError(err, 'Connection failed'));
    }
  }, [activateSavedVaultEntry, selectedProvider, enterEditor]);

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
      enterEditor();
      setConnectionState('connected');
    } catch (err) {
      setConnectionState('error');
      setErrorMessage(toUserFacingError(err, 'Connection failed'));
    }
  }, [enterEditor]);

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

  const handleQuickStartLaunch = useCallback(async (schema: QuickStartSchema, dataSize: DataSize) => {
    // Shared finalize for both the SSE and non-SSE paths: /api/database/create-
    // quickstart self-binds the freshly created SQLite database on the server
    // side before it reports the connection string, so by the time we get here
    // BifrostQL is already pointed at the new file. SQLite connection strings
    // carry no credentials so this side-band bind does not re-introduce the
    // password-over-HTTP issue that prompted deleting /api/connection/set.
    const activateSqliteConnection = (info: ConnectionInfo) => {
      setConnectionInfo(info);
      saveSession(info);
      const updated = [...recentConnections.filter((c) => c.connectionString !== info.connectionString), info];
      setRecentConnections(updated.slice(0, 5));
      saveRecentConnections(updated.slice(0, 5));

      enterEditor();
      setConnectionState('connected');
    };

    try {
      setConnectionState('connecting');
      setErrorMessage(null);
      setIsLaunching(true);
      setLaunchProgress('Starting...');

      const response = await fetch(API_QUICKSTART, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'Accept': 'text/event-stream' },
        body: JSON.stringify({ schema, dataSize: dataSize }),
      });

      if (!response.ok) {
        const result = await response.json();
        throw new Error(result.error || 'Failed to create quickstart database');
      }

      const contentType = response.headers.get('content-type') ?? '';
      if (contentType.includes('text/event-stream') && response.body) {
        const connectionString = await parseQuickstartStream(response, setLaunchProgress);

        if (connectionString) {
          activateSqliteConnection({
            id: Date.now().toString(),
            name: `QuickStart - ${schema}`,
            connectionString,
            connectedAt: new Date().toISOString(),
            server: 'localhost',
            database: schema,
            provider: 'sqlite',
          });
        } else {
          throw new Error('No connection string received from server');
        }
      } else {
        const result = await response.json();
        activateSqliteConnection({
          id: Date.now().toString(),
          name: `QuickStart - ${schema}`,
          connectionString: result.connectionString,
          connectedAt: new Date().toISOString(),
          server: 'localhost',
          database: schema,
          provider: 'sqlite',
        });
      }
    } catch (err) {
      setConnectionState('error');
      setErrorMessage(toUserFacingError(err, 'Failed to create quickstart database'));
    } finally {
      setIsLaunching(false);
      setLaunchProgress('');
    }
  }, [recentConnections, enterEditor]);

  const handleClearRecentConnections = useCallback(() => {
    setRecentConnections([]);
    saveRecentConnections([]);
  }, []);

  const handleDisconnect = useCallback(() => {
    fetch('/api/ssh/disconnect', { method: 'POST' }).catch(() => {});
    setConnectionInfo(null);
    saveSession(null);
    setConnectionState('idle');
    setErrorMessage(null);
    setSelectedProvider(null);
  }, []);

  return {
    connectionState,
    setConnectionState,
    errorMessage,
    setErrorMessage,
    connectionInfo,
    recentConnections,
    vaultServers,
    selectedProvider,
    setSelectedProvider,
    isLaunching,
    launchProgress,
    handleTestConnection,
    handleConnect,
    handleConnectVaultServer,
    handleSelectRecentConnection,
    handleQuickStartLaunch,
    handleClearRecentConnections,
    handleDisconnect,
  };
}
