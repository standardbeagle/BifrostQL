import { useState, useCallback, useEffect } from 'react';
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
} from './connection';
import { saveSession, loadSession } from './connection/session';
import './connection/connection.css';
import './app.css';

// API endpoints
const API_TEST_CONNECTION = '/api/connection/test';
const API_QUICKSTART = '/api/database/create-quickstart';

type AppView = 'welcome' | 'quickstart' | 'provider-select' | 'connect' | 'editor';

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

  // Restore backend connection if we have a saved session (survives page reloads)
  useEffect(() => {
    if (!restored) return;
    fetch('/api/connection/set', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        connectionString: restored.connectionString,
        provider: restored.provider,
      }),
    }).catch(() => {
      // Backend may not be ready yet — health check will handle recovery
    });
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  // Periodic health check — detects backend restarts and auto-recovers
  const [backendDown, setBackendDown] = useState(false);
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

      // Update the backend connection
      const updateResponse = await fetch('/api/connection/set', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ connectionString, provider: resolvedProvider }),
      });

      if (!updateResponse.ok) {
        const updateResult = await updateResponse.json().catch(() => ({}));
        throw new Error(updateResult.error || 'Failed to update connection');
      }

      const info: ConnectionInfo = {
        id: Date.now().toString(),
        name: connectionName,
        connectionString,
        connectedAt: new Date().toISOString(),
        server: connectionName,
        database: connectionName,
        provider: resolvedProvider,
      };

      setConnectionInfo(info);
      saveSession(info);
      const updated = [...recentConnections.filter((c) => c.connectionString !== connectionString), info];
      setRecentConnections(updated.slice(0, 5));
      saveRecentConnections(updated.slice(0, 5));

      setEditorKey((k) => k + 1);
      setCurrentView('editor');
      setConnectionState('connected');
    } catch (err) {
      setConnectionState('error');
      const msg = err instanceof Error ? err.message : 'Connection failed';
      setErrorMessage(msg.includes('Failed to fetch')
        ? 'Cannot reach the backend server. It may have crashed or failed to start.'
        : msg);
    }
  }, [recentConnections, selectedProvider]);

  const handleSelectRecentConnection = useCallback((connection: ConnectionInfo) => {
    const provider = connection.provider ?? 'sqlserver';
    setSelectedProvider(provider);
    handleConnect(connection.connectionString, connection.name, provider);
  }, [handleConnect]);

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
          await fetch('/api/connection/set', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ connectionString, provider: 'sqlite' }),
          });

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

        await fetch('/api/connection/set', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ connectionString: result.connectionString, provider: 'sqlite' }),
        });

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
          <button
            className="bifrost-disconnect-button"
            onClick={handleBack}
          >
            Disconnect
          </button>
        </div>
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
      />
    </div>
  );
}
