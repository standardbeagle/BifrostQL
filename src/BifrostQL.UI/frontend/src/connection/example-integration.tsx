/**
 * Example Integration of Connection UI with BifrostQL
 *
 * This file demonstrates how to integrate the connection components
 * with the existing BifrostQL desktop application.
 *
 * Replace the content of main.tsx with this code to enable the connection UI.
 */

import React, { useState, useCallback, useEffect } from 'react';
import ReactDOM from 'react-dom/client';
import Editor from '@standardbeagle/edit-db';
import {
  WelcomePanel,
  ConnectionForm,
  ConnectionInfo,
  TestDatabaseDialog,
  TestDatabaseTemplate,
  TestDatabaseProgress,
  saveRecentConnections,
  loadRecentConnections
} from './index';
import './connection/connection.css';
import './app.css';

type AppState = 'welcome' | 'connecting' | 'connected' | 'creating-test-db';

function App() {
  const [appState, setAppState] = useState<AppState>('welcome');
  const [currentConnection, setCurrentConnection] = useState<ConnectionInfo | null>(null);
  const [recentConnections, setRecentConnections] = useState<ConnectionInfo[]>([]);
  const [isTestDbDialogOpen, setIsTestDbDialogOpen] = useState(false);
  const [isCreatingTestDb, setIsCreatingTestDb] = useState(false);
  const [testDbProgress, setTestDbProgress] = useState<TestDatabaseProgress | null>(null);
  const [testDbError, setTestDbError] = useState<string | null>(null);

  // Load recent connections on mount
  useEffect(() => {
    const loaded = loadRecentConnections();
    setRecentConnections(loaded);
  }, []);

  // Handle connection from connection form or recent connection
  const handleConnect = useCallback((connectionString: string, connectionName: string) => {
    // Parse connection string to extract server and database
    const serverMatch = connectionString.match(/Server=([^;]+)/);
    const dbMatch = connectionString.match(/Database=([^;]+)/);
    const server = serverMatch ? serverMatch[1] : 'localhost';
    const database = dbMatch ? dbMatch[1] : 'unknown';

    const connectionInfo: ConnectionInfo = {
      id: Date.now().toString(),
      name: connectionName,
      connectionString,
      connectedAt: new Date().toISOString(),
      server,
      database,
    };

    // Save to recent connections
    const updated = [connectionInfo, ...recentConnections.filter(c => c.id !== connectionInfo.id)].slice(0, 5);
    setRecentConnections(updated);
    saveRecentConnections(updated);

    setCurrentConnection(connectionInfo);
    setAppState('connected');

    // In a real implementation, you would trigger the backend connection here
    console.log('Connecting with:', connectionString);
  }, [recentConnections]);

  // Handle test connection (optional - requires backend API)
  const handleTestConnection = useCallback(async (_connectionString: string): Promise<boolean> => {
    try {
      // This would call a backend API to test the connection
      // For now, simulate a test
      await new Promise(resolve => setTimeout(resolve, 1000));
      return Math.random() > 0.3; // Simulate 70% success rate
    } catch (error) {
      console.error('Connection test failed:', error);
      return false;
    }
  }, []);

  // Handle recent connection selection
  const handleSelectRecentConnection = useCallback((connection: ConnectionInfo) => {
    handleConnect(connection.connectionString, connection.name);
  }, [handleConnect]);

  // Handle clear recent connections
  const handleClearRecentConnections = useCallback(() => {
    setRecentConnections([]);
    saveRecentConnections([]);
  }, []);

  // Handle create test database
  const handleCreateTestDatabase = useCallback(async (template: TestDatabaseTemplate) => {
    setIsCreatingTestDb(true);
    setTestDbError(null);

    try {
      // Simulate database creation progress
      const stages = [
        { stage: 'Creating database...', percent: 20, message: `Initializing ${template} database` },
        { stage: 'Creating tables...', percent: 40, message: 'Setting up schema' },
        { stage: 'Adding data...', percent: 60, message: 'Populating sample data' },
        { stage: 'Finalizing...', percent: 80, message: 'Applying constraints' },
        { stage: 'Complete', percent: 100, message: 'Database ready!', connectionString: `Server=localhost;Database=${template}_test;Trusted_Connection=Yes;TrustServerCertificate=True` },
      ];

      for (const progress of stages) {
        await new Promise(resolve => setTimeout(resolve, 800));
        setTestDbProgress(progress);
      }

      // Auto-connect after creation
      setTimeout(() => {
        if (stages[4].connectionString) {
          handleConnect(stages[4].connectionString, `${template.replace('-', ' ')} test database`);
        }
        setIsTestDbDialogOpen(false);
      }, 500);

    } catch (error) {
      setTestDbError(error instanceof Error ? error.message : 'Failed to create test database');
    } finally {
      setIsCreatingTestDb(false);
    }
  }, [handleConnect]);

  // Render main editor when connected
  const graphqlUri = `${window.location.origin}/graphql`;

  return (
    <>
      {appState === 'welcome' && (
        <WelcomePanel
          onConnectClick={() => setAppState('connecting')}
          onCreateTestDatabase={() => setIsTestDbDialogOpen(true)}
          recentConnections={recentConnections}
          onSelectRecentConnection={handleSelectRecentConnection}
          onClearRecentConnections={handleClearRecentConnections}
        />
      )}

      {appState === 'connecting' && (
        <div style={{ minHeight: '100vh', display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
          <ConnectionForm
            onConnect={handleConnect}
            onTestConnection={handleTestConnection}
          />
          <button
            onClick={() => setAppState('welcome')}
            style={{
              position: 'absolute',
              top: '1rem',
              left: '1rem',
              padding: '0.5rem 1rem',
              background: 'var(--color-bg-secondary, #1e293b)',
              border: '1px solid var(--color-border, #334155)',
              borderRadius: '0.5rem',
              color: 'var(--color-text-primary, #e2e8f0)',
              cursor: 'pointer',
            }}
          >
            ← Back
          </button>
        </div>
      )}

      {appState === 'connected' && currentConnection && (
        <div className="app-container">
          <div
            style={{
              position: 'absolute',
              top: '1rem',
              left: '1rem',
              right: '1rem',
              display: 'flex',
              justifyContent: 'space-between',
              alignItems: 'center',
              padding: '0.5rem 1rem',
              background: 'var(--color-bg-secondary, #1e293b)',
              border: '1px solid var(--color-border, #334155)',
              borderRadius: '0.5rem',
              zIndex: 1000,
            }}
          >
            <span style={{ color: 'var(--color-text-primary, #e2e8f0)', fontSize: '0.875rem' }}>
              Connected to: {currentConnection.name}
            </span>
            <button
              onClick={() => setAppState('welcome')}
              style={{
                padding: '0.5rem 1rem',
                background: 'var(--color-bg-tertiary, #334155)',
                border: '1px solid var(--color-border, #475569)',
                borderRadius: '0.5rem',
                color: 'var(--color-text-primary, #e2e8f0)',
                cursor: 'pointer',
                fontSize: '0.875rem',
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
      )}

      <TestDatabaseDialog
        isOpen={isTestDbDialogOpen}
        onClose={() => {
          setIsTestDbDialogOpen(false);
          setTestDbProgress(null);
          setTestDbError(null);
        }}
        onCreate={handleCreateTestDatabase}
        isCreating={isCreatingTestDb}
        progress={testDbProgress}
        error={testDbError}
      />
    </>
  );
}

// Render the app
ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>
);
