import React, { useState, useCallback } from 'react';
import ReactDOM from 'react-dom/client';
import Editor from '@standardbeagle/edit-db';
import {
  WelcomePanel,
  ConnectionForm,
  ConnectionInfo,
  ConnectionState,
  Provider,
  PROVIDERS,
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
  const [connectionState, setConnectionState] = useState<ConnectionState>('idle');
  const [currentView, setCurrentView] = useState<AppView>('welcome');
  const [recentConnections, setRecentConnections] = useState<ConnectionInfo[]>(() => loadRecentConnections());
  const [connectionInfo, setConnectionInfo] = useState<ConnectionInfo | null>(null);
  const [selectedProvider, setSelectedProvider] = useState<Provider | null>(null);

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

      const response = await fetch(API_QUICKSTART, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ schema, dataSize }),
      });

      if (!response.ok) {
        const result = await response.json();
        throw new Error(result.error || 'Failed to create quickstart database');
      }

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
    } catch {
      setConnectionState('error');
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
        <div style={{ padding: '2rem', textAlign: 'center' }}>
          <h2>Quick Start</h2>
          <p>Select a schema template to get started instantly.</p>
          <button
            className="bifrost-back-button"
            onClick={() => handleQuickStartLaunch('blog', 'sample')}
          >
            Launch Blog (Sample)
          </button>
        </div>
        <button
          className="bifrost-back-button"
          onClick={handleBack}
        >
          &larr; Back
        </button>
      </div>
    );
  }

  if (currentView === 'provider-select') {
    return (
      <div className="bifrost-connection-container">
        <div style={{ padding: '2rem', textAlign: 'center' }}>
          <h2>Select Database Provider</h2>
          <p>Choose your database type to configure the connection.</p>
          <div style={{ display: 'flex', gap: '1rem', justifyContent: 'center', flexWrap: 'wrap', marginTop: '1rem' }}>
            {PROVIDERS.map((p) => (
              <button
                key={p.id}
                onClick={() => handleProviderSelect(p.id)}
                style={{
                  padding: '1rem',
                  border: '1px solid var(--color-border, #334155)',
                  borderRadius: '0.5rem',
                  backgroundColor: 'var(--color-bg-secondary, #1e293b)',
                  color: 'var(--color-text-primary, #e2e8f0)',
                  cursor: 'pointer',
                  minWidth: '120px',
                }}
              >
                <div style={{ fontSize: '1.5rem', fontWeight: '700' }}>{p.icon}</div>
                <div>{p.name}</div>
              </button>
            ))}
          </div>
        </div>
        <button
          className="bifrost-back-button"
          onClick={handleBack}
        >
          &larr; Back
        </button>
      </div>
    );
  }

  if (currentView === 'connect') {
    return (
      <div className="bifrost-connection-container">
        <ConnectionForm
          onConnect={handleConnect}
          onTestConnection={handleTestConnection}
          initialState={connectionState}
        />
        <button
          className="bifrost-back-button"
          onClick={handleBack}
        >
          &larr; Back
        </button>
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
