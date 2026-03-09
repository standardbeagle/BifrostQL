import React, { useState, useCallback } from 'react';
import ReactDOM from 'react-dom/client';
import Editor from '@standardbeagle/edit-db';
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
import './connection/connection.css';
import './app.css';

// API endpoints
const API_TEST_CONNECTION = '/api/connection/test';
const API_QUICKSTART = '/api/database/quickstart';

type AppView = 'welcome' | 'quickstart' | 'provider-select' | 'connect' | 'editor';

function App() {
  const [, setConnectionState] = useState<ConnectionState>('idle');
  const [currentView, setCurrentView] = useState<AppView>('welcome');
  const [recentConnections, setRecentConnections] = useState<ConnectionInfo[]>(() => loadRecentConnections());
  const [connectionInfo, setConnectionInfo] = useState<ConnectionInfo | null>(null);
  const [selectedProvider, setSelectedProvider] = useState<Provider | null>(null);
  const [isLaunching, setIsLaunching] = useState(false);
  const [launchProgress, setLaunchProgress] = useState('');

  const graphqlUri = `${window.location.origin}/graphql`;

  const handleTestConnection = useCallback(async (connectionString: string): Promise<boolean> => {
    try {
      setConnectionState('testing');
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
    } catch {
      return false;
    } finally {
      setConnectionState('idle');
    }
  }, []);

  const handleConnect = useCallback(async (connectionString: string, connectionName: string) => {
    try {
      setConnectionState('connecting');

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
        body: JSON.stringify({ connectionString }),
      });

      if (!updateResponse.ok) {
        throw new Error('Failed to update connection');
      }

      const info: ConnectionInfo = {
        id: Date.now().toString(),
        name: connectionName,
        connectionString,
        connectedAt: new Date().toISOString(),
        server: connectionName,
        database: connectionName,
        provider: selectedProvider ?? 'sqlserver',
      };

      setConnectionInfo(info);
      const updated = [...recentConnections.filter((c) => c.connectionString !== connectionString), info];
      setRecentConnections(updated.slice(0, 5));
      saveRecentConnections(updated.slice(0, 5));

      setCurrentView('editor');
      setConnectionState('connected');
    } catch {
      setConnectionState('error');
    }
  }, [recentConnections, selectedProvider]);

  const handleSelectRecentConnection = useCallback((connection: ConnectionInfo) => {
    setSelectedProvider(connection.provider ?? 'sqlserver');
    handleConnect(connection.connectionString, connection.name);
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
          const updated = [...recentConnections.filter((c) => c.connectionString !== connectionString), info];
          setRecentConnections(updated.slice(0, 5));
          saveRecentConnections(updated.slice(0, 5));

          setCurrentView('editor');
          setConnectionState('connected');
        } else {
          throw new Error('No connection string received from server');
        }
      } else {
        const result = await response.json();

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
        const updated = [...recentConnections.filter((c) => c.connectionString !== result.connectionString), info];
        setRecentConnections(updated.slice(0, 5));
        saveRecentConnections(updated.slice(0, 5));

        setCurrentView('editor');
        setConnectionState('connected');
      }
    } catch {
      setConnectionState('error');
    } finally {
      setIsLaunching(false);
      setLaunchProgress('');
    }
  }, [recentConnections]);

  const handleBack = useCallback(() => {
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
        setConnectionState('idle');
        setSelectedProvider(null);
        break;
      default:
        setCurrentView('welcome');
    }
  }, [currentView]);

  if (currentView === 'quickstart') {
    return (
      <div className="bifrost-connection-container">
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
          <h1>BifrostQL</h1>
          {connectionInfo && <span className="bifrost-database-info">{connectionInfo.name}</span>}
          <button
            className="bifrost-disconnect-button"
            onClick={handleBack}
          >
            Disconnect
          </button>
        </div>
        <Editor
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
      <WelcomePanel
        onConnectClick={() => setCurrentView('provider-select')}
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

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>
);
