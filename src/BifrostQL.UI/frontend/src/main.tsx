import React, { useState, useCallback } from 'react';
import ReactDOM from 'react-dom/client';
import Editor from '@standardbeagle/edit-db';
import {
  WelcomePanel,
  ConnectionForm,
  TestDatabaseDialog,
  ConnectionInfo,
  TestDatabaseTemplate,
  ConnectionState,
  saveRecentConnections,
  loadRecentConnections,
} from './connection';
import './connection/connection.css';
import './app.css';

// API endpoints
const API_TEST_CONNECTION = '/api/connection/test';
const API_CREATE_DATABASE = '/api/database/create';

function App() {
  const [connectionState, setConnectionState] = useState<ConnectionState>('idle');
  const [currentView, setCurrentView] = useState<'welcome' | 'connect' | 'editor'>('welcome');
  const [recentConnections, setRecentConnections] = useState<ConnectionInfo[]>(() => loadRecentConnections());
  const [showTestDbDialog, setShowTestDbDialog] = useState(false);
  const [testDbProgress, setTestDbProgress] = useState<any>(null);
  const [connectionInfo, setConnectionInfo] = useState<ConnectionInfo | null>(null);

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
    } catch (err) {
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
      };

      setConnectionInfo(info);
      const updated = [...recentConnections.filter((c) => c.connectionString !== connectionString), info];
      setRecentConnections(updated.slice(0, 5));
      saveRecentConnections(updated.slice(0, 5));

      setCurrentView('editor');
      setConnectionState('connected');
    } catch (err) {
      setConnectionState('error');
    }
  }, [recentConnections]);

  const handleCreateTestDatabase = useCallback(async (template: TestDatabaseTemplate) => {
    try {
      setTestDbProgress({ stage: 'Initializing...', percent: 0, message: 'Starting database creation...' });

      const response = await fetch(API_CREATE_DATABASE, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ template }),
      });

      if (!response.ok) {
        const result = await response.json();
        throw new Error(result.error || 'Failed to create database');
      }

      // Handle streaming progress
      const reader = response.body?.getReader();
      if (!reader) {
        throw new Error('No response stream');
      }

      const decoder = new TextDecoder();
      let buffer = '';

      while (true) {
        const { done, value } = await reader.read();
        if (done) break;

        buffer += decoder.decode(value, { stream: true });
        const lines = buffer.split('\n');
        buffer = lines.pop() || '';

        for (const line of lines) {
          if (line.startsWith('data: ')) {
            try {
              const data = JSON.parse(line.slice(6));
              setTestDbProgress(data);
            } catch (e) {
              console.error('Failed to parse progress:', e);
            }
          }
        }
      }

      setTestDbProgress((prev: any) => ({ ...prev, percent: 100, stage: 'Complete!' }));
      setShowTestDbDialog(false);
    } catch (err) {
      setTestDbProgress((prev: any) => ({ ...prev, error: err instanceof Error ? err.message : 'Creation failed' }));
    }
  }, []);

  const handleSelectRecentConnection = useCallback((connection: ConnectionInfo) => {
    handleConnect(connection.connectionString, connection.name);
  }, [handleConnect]);

  const handleConnectAfterCreate = useCallback((connectionString: string) => {
    setShowTestDbDialog(false);
    const match = connectionString.match(/Database=([^;]+)/);
    const dbName = match ? match[1] : 'Test Database';
    handleConnect(connectionString, dbName);
  }, [handleConnect]);

  // Show connection form
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
          onClick={() => setCurrentView('welcome')}
        >
          ← Back
        </button>
      </div>
    );
  }

  // Show database editor when connected
  if (currentView === 'editor') {
    return (
      <div className="app-container">
        <div className="bifrost-header">
          <h1>BifrostQL</h1>
          {connectionInfo && <span className="bifrost-database-info">{connectionInfo.name}</span>}
          <button
            className="bifrost-disconnect-button"
            onClick={() => {
              setCurrentView('welcome');
              setConnectionInfo(null);
              setConnectionState('idle');
            }}
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

  // Show welcome panel by default
  return (
    <div className="bifrost-welcome-container">
      <WelcomePanel
        onConnectClick={() => setCurrentView('connect')}
        onCreateTestDatabase={() => setShowTestDbDialog(true)}
        recentConnections={recentConnections}
        onSelectRecentConnection={handleSelectRecentConnection}
        onClearRecentConnections={() => {
          setRecentConnections([]);
          saveRecentConnections([]);
        }}
      />

      {showTestDbDialog && (
        <TestDatabaseDialog
          isOpen={showTestDbDialog}
          onClose={() => {
            setShowTestDbDialog(false);
            setTestDbProgress(null);
          }}
          onCreate={handleCreateTestDatabase}
          onConnectAfterCreate={handleConnectAfterCreate}
          isCreating={!!testDbProgress && testDbProgress.percent < 100}
          progress={testDbProgress}
          error={testDbProgress?.error || null}
        />
      )}
    </div>
  );
}

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>
);
